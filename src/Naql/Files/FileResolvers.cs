using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Files;

/// <summary>
/// Resolves <c>file:</c> references: a file into its <see cref="FileSource"/>, a folder into the sources of all its
/// files, with their paths inside the folder.
/// </summary>
/// <remarks>
/// Files a transfer is still writing (<see cref="FileTarget.PartialExtension"/>) and checkpoints
/// (<see cref="FileCheckpointStore.Extension"/>) aren't content, so a folder leaves them out.
/// </remarks>
public sealed class FileSourceResolver : ISourceResolver
{
    /// <summary>
    /// A resolver that includes the files of subfolders, or only the folder's own files when
    /// <paramref name="recursive"/> is <c>false</c>.
    /// </summary>
    public FileSourceResolver(bool recursive = true)
    {
        Recursive = recursive;
    }

    /// <summary>
    /// Whether a folder includes the files of its subfolders.
    /// </summary>
    public bool Recursive { get; }

    /// <inheritdoc />
    public bool CanResolve(Uri reference) => reference is { IsAbsoluteUri: true, IsFile: true };

    /// <inheritdoc />
    public async IAsyncEnumerable<ResolvedSource> ResolveAsync(Uri reference, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var path = reference.LocalPath;
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            yield return ResolvedSource.ToSource(FileSource.At(file.FullName), "", Describe(file));
            yield break;
        }
        if (!Directory.Exists(path))
        {
            throw new TransferException($"There is no file or folder '{path}'", TransferErrorKind.NotFound, side: TransferSide.Source);
        }

        var root = new DirectoryInfo(path);
        var rootLength = root.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1;
        using var files = Enumerate(root).GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo file;
            try
            {
                if (!files.MoveNext())
                {
                    yield break;
                }
                file = files.Current;
            }
            catch (Exception ex) when (FileErrors.Map(ex, TransferSide.Source) is { } error)
            {
                throw error;
            }

            if (file.Name.EndsWith(FileTarget.PartialExtension, StringComparison.OrdinalIgnoreCase) ||
                file.Name.EndsWith(FileCheckpointStore.Extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var relative = file.FullName.Substring(rootLength).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            yield return ResolvedSource.ToSource(FileSource.At(file.FullName), relative, Describe(file));
            // Listing is synchronous; let the caller work on what came out so far
            await Task.Yield();
        }
    }

    private IEnumerable<FileInfo> Enumerate(DirectoryInfo root)
    {
        return root.EnumerateFiles("*", Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
    }

    private static SourceInfo Describe(FileInfo file) => new()
    {
        Length = file.Length,
        Name = file.Name,
        LastModified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
        Identity = file.FullName
    };
}

/// <summary>
/// Resolves <c>file:</c> destinations into <see cref="FileTarget"/>s.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A destination that ends with <c>/</c>, or is an existing folder, is a folder: content with a relative path
/// goes to that path inside it, and a single item is named from its source, like <see cref="FileTarget.InDirectory"/>.</item>
/// <item>Any other destination is the file a single item is written to, like <see cref="FileTarget.At"/>.</item>
/// <item>Every segment of a relative path is made safe the way <see cref="FileTarget.InDirectory"/> makes names safe,
/// on every OS, and the path can't leave the folder.</item>
/// </list>
/// </remarks>
public sealed class FileTargetResolver : ITargetResolver
{
    /// <summary>
    /// A resolver whose targets do <paramref name="conflict"/> when a file already exists.
    /// </summary>
    public FileTargetResolver(FileConflict conflict = FileConflict.Overwrite)
    {
        Conflict = conflict;
    }

    /// <summary>
    /// What the targets do when a file already exists.
    /// </summary>
    public FileConflict Conflict { get; }

    /// <inheritdoc />
    public bool CanResolve(Uri destination) => destination is { IsAbsoluteUri: true, IsFile: true };

    /// <inheritdoc />
    public ValueTask<ITransferTarget> ResolveAsync(Uri destination, string relativePath, SourceInfo? info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var segments = RelativePath.Split(relativePath);
        var path = destination.LocalPath;
        var isFolder = destination.AbsolutePath.EndsWith('/') || Directory.Exists(path);

        if (segments.Count == 0)
        {
            return new ValueTask<ITransferTarget>(isFolder ? FileTarget.InDirectory(path, conflict: Conflict) : FileTarget.At(path, Conflict));
        }
        if (!isFolder && File.Exists(path))
        {
            throw new TransferException($"'{path}' is a file, so it can't hold '{relativePath}'", TransferErrorKind.AlreadyExists, side: TransferSide.Target);
        }

        var full = path;
        foreach (var segment in segments)
        {
            full = Path.Combine(full, FileNames.ToSafeName(segment) ?? "_");
        }
        return new ValueTask<ITransferTarget>(FileTarget.At(full, Conflict));
    }
}
