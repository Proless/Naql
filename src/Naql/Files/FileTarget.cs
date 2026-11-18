using Naql.Streams;
using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Files;

/// <summary>
/// What a <see cref="FileTarget"/> does when the file already exists.
/// </summary>
public enum FileConflict
{
    /// <summary>Replace the existing file.</summary>
    Overwrite,

    /// <summary>Fail with <see cref="TransferErrorKind.AlreadyExists"/> before transferring anything.</summary>
    Fail,

    /// <summary>Keep the existing file and write to a new name, such as <c>report (1).pdf</c>.</summary>
    KeepBoth
}

/// <summary>
/// Writes a local file, for example a download.
/// </summary>
/// <remarks>
/// The content is written to <c>name.naql-part</c> next to the file and renamed when the transfer completes, so the
/// file never exists half-written. Parts are written in parallel at their offsets. A partial file is kept when the
/// transfer fails or is canceled, and its path is the session's <see cref="ITargetSession.ResumeToken"/>.
/// </remarks>
public sealed class FileTarget : ITransferTarget
{
    /// <summary>
    /// Added to the file name while the content is being written.
    /// </summary>
    public const string PartialExtension = ".naql-part";

    private const string DefaultName = "download";

    private readonly string? _file;
    private readonly string? _directory;
    private readonly Func<SourceInfo, string?>? _nameSelector;

    private FileTarget(string? file, string? directory, Func<SourceInfo, string?>? nameSelector, FileConflict conflict)
    {
        _file = file;
        _directory = directory;
        _nameSelector = nameSelector;
        Conflict = conflict;
    }

    /// <summary>
    /// Writes the file at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The file to write</param>
    /// <param name="conflict">What to do when the file already exists</param>
    public static FileTarget At(string path, FileConflict conflict = FileConflict.Overwrite)
    {
        return new FileTarget(System.IO.Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path))), null, null, conflict);
    }

    /// <summary>
    /// Writes a file in <paramref name="directory"/>, named by <paramref name="nameSelector"/> or else by
    /// <see cref="SourceInfo.Name"/>. The name is made safe first, so it always stays inside the directory.
    /// </summary>
    /// <param name="directory">The directory to write the file in; it's created when missing</param>
    /// <param name="nameSelector">Picks the file name; <c>null</c> or a <c>null</c> result uses the source's name</param>
    /// <param name="conflict">What to do when the file already exists</param>
    public static FileTarget InDirectory(string directory, Func<SourceInfo, string?>? nameSelector = null, FileConflict conflict = FileConflict.Overwrite)
    {
        return new FileTarget(null, System.IO.Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory))), nameSelector, conflict);
    }

    /// <summary>
    /// What to do when the file already exists.
    /// </summary>
    public FileConflict Conflict { get; }

    /// <inheritdoc />
    public TargetCapabilities Capabilities { get; } = new()
    {
        ParallelParts = true,
        ResumableParts = true,
        Resumable = true,
        Verifiable = true
    };

    /// <summary>
    /// The full path of the file, once a transfer has opened the target.
    /// </summary>
    public string? Path { get; private set; }

    /// <inheritdoc />
    public Task<ITargetSession> OpenAsync(TargetOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            string path;
            string partial;
            if (request.ResumeToken is string token)
            {
                // Continue writing the partial file of an earlier transfer
                if (!token.EndsWith(PartialExtension, StringComparison.Ordinal) || !File.Exists(token))
                {
                    throw new TransferException($"There is no partial file '{token}' to continue", TransferErrorKind.NotFound, side: TransferSide.Target);
                }
                partial = token;
                path = token[..^PartialExtension.Length];
            }
            else
            {
                path = ApplyConflict(ResolvePath(request.Source));
                partial = path + PartialExtension;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

                using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                if (request.Source.Length is long length)
                {
                    // Reserve the space up front: parallel parts write at their offsets, and a full disk fails early
                    file.SetLength(length);
                }
            }

            Path = path;
            return Task.FromResult<ITargetSession>(new FileSession(path, partial, request.Source.Length, Conflict));
        }
        catch (Exception ex) when (FileErrors.Map(ex, TransferSide.Target) is { } error)
        {
            throw error;
        }
    }

    private string ResolvePath(SourceInfo source)
    {
        if (_file != null)
        {
            return _file;
        }

        var name = FileNames.ToSafeName(_nameSelector?.Invoke(source) ?? source.Name) ?? DefaultName;
        return System.IO.Path.Combine(_directory!, name);
    }

    private string ApplyConflict(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        switch (Conflict)
        {
            case FileConflict.Fail:
                throw AlreadyExists(path);
            case FileConflict.KeepBoth:
                var directory = System.IO.Path.GetDirectoryName(path)!;
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                var extension = System.IO.Path.GetExtension(path);
                for (var i = 1; ; i++)
                {
                    var candidate = System.IO.Path.Combine(directory, $"{name} ({i}){extension}");
                    if (!File.Exists(candidate) && !File.Exists(candidate + PartialExtension))
                    {
                        return candidate;
                    }
                }
            default:
                return path;
        }
    }

    private static TransferException AlreadyExists(string path)
    {
        return new TransferException($"The file '{path}' already exists", TransferErrorKind.AlreadyExists, side: TransferSide.Target);
    }

    private sealed class FileSession : ITargetSession, IVerifiableSession
    {
        private const int CopyBufferSize = 81920;

        private readonly string _path;
        private readonly string _partial;
        private readonly long? _length;
        private readonly FileConflict _conflict;

        public FileSession(string path, string partial, long? length, FileConflict conflict)
        {
            _path = path;
            _partial = partial;
            _length = length;
            _conflict = conflict;
        }

        public string? ResumeToken => _partial;

        public async Task WritePartAsync(ByteRange range, Stream content, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            try
            {
                // Positional writes with no buffer of their own: every byte read from the content is in the file before
                // the next read, which lets a failed part continue where it stopped, and parallel parts share no position
                using var file = File.OpenHandle(_partial, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
                var position = range.Start;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await RandomAccess.WriteAsync(file, buffer.AsMemory(0, read), position, cancellationToken).ConfigureAwait(false);
                    position += read;
                }
            }
            catch (Exception ex) when (FileErrors.Map(ex, TransferSide.Target) is { } error)
            {
                throw error;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_length is long length)
                {
                    using var file = new FileStream(_partial, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    if (file.Length != length)
                    {
                        file.SetLength(length);
                    }
                }

                if (File.Exists(_path))
                {
                    // Someone created the file while the transfer ran
                    if (_conflict == FileConflict.Fail)
                    {
                        throw AlreadyExists(_path);
                    }
                    File.Delete(_path);
                }
                File.Move(_partial, _path);
                return Task.CompletedTask;
            }
            catch (Exception ex) when (FileErrors.Map(ex, TransferSide.Target) is { } error)
            {
                throw error;
            }
        }

        public Task AbortAsync(CancellationToken cancellationToken)
        {
            if (File.Exists(_partial))
            {
                File.Delete(_partial);
            }
            return Task.CompletedTask;
        }

        public async Task<byte[]> ComputeHashAsync(HashAlgorithmName algorithm, long length, CancellationToken cancellationToken)
        {
            try
            {
                using var file = new FileStream(_partial, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, CopyBufferSize, useAsync: true);
                return await Hashing.HashAsync(file, algorithm, length, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (FileErrors.Map(ex, TransferSide.Target) is { } error)
            {
                throw error;
            }
        }

        public void Dispose()
        {
            // Each part opens and closes the file itself, so there is nothing left open
        }
    }
}
