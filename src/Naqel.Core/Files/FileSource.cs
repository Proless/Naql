using Naqel.Streams;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Naqel.Files;

/// <summary>
/// Reads a local file, for example to upload it.
/// </summary>
public sealed class FileSource : ITransferSource
{
    private const int BufferSize = 81920;

    private FileSource(string path)
    {
        Path = System.IO.Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
    }

    /// <summary>
    /// The full path of the file.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Reads the file at <paramref name="path"/>.
    /// </summary>
    public static FileSource At(string path) => new(path);

    /// <inheritdoc />
    public Task<SourceInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(Describe(new FileInfo(Path)));
        }
        catch (Exception ex) when (FileErrors.Map(ex, TransferSide.Source) is { } error)
        {
            throw error;
        }
    }

    /// <inheritdoc />
    public Task<SourceRead> OpenReadAsync(ReadRequest request, CancellationToken cancellationToken)
    {
        FileStream? file = null;
        try
        {
            file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);
            var info = Describe(new FileInfo(Path));
            if (request.IfVersion != null && request.IfVersion != info.Version)
            {
                throw new TransferException("The file changed", TransferErrorKind.SourceChanged, side: TransferSide.Source);
            }

            file.Seek(request.Range.Start, SeekOrigin.Begin);
            var stream = new StreamSlice(file, request.Range.Length, ownsInner: true);
            return Task.FromResult(new SourceRead(stream, info));
        }
        catch (Exception ex)
        {
            file?.Dispose();
            if (FileErrors.Map(ex, TransferSide.Source) is { } error)
            {
                throw error;
            }
            throw;
        }
    }

    private static SourceInfo Describe(FileInfo file)
    {
        if (!file.Exists)
        {
            throw new FileNotFoundException($"The file '{file.FullName}' doesn't exist", file.FullName);
        }

        var lastWrite = file.LastWriteTimeUtc;
        return new SourceInfo
        {
            Length = file.Length,
            SupportsRanges = true,
            IsRepeatable = true,
            Identity = file.FullName,
            Version = FormattableString.Invariant($"{file.Length}:{lastWrite.Ticks}"),
            Name = file.Name,
            LastModified = new DateTimeOffset(lastWrite, TimeSpan.Zero)
        };
    }
}
