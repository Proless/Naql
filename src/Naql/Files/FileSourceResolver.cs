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
    public async IAsyncEnumerable<Resolved> ResolveAsync(Uri reference, ResolveContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(context);
        var recursive = context.Get<FileResolveOptions>()?.Recursive ?? Recursive;
        var path = reference.LocalPath;
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            yield return new ResolvedSource(FileSource.At(file.FullName), "", Describe(file));
            yield break;
        }
        if (!Directory.Exists(path))
        {
            throw new TransferException($"There is no file or folder '{path}'", TransferErrorKind.NotFound, side: TransferSide.Source);
        }

        var root = new DirectoryInfo(path);
        var rootLength = root.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1;
        using var files = root.EnumerateFiles("*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).GetEnumerator();
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
            yield return new ResolvedSource(FileSource.At(file.FullName), relative, Describe(file));
            // Listing is synchronous; let the caller work on what came out so far
            await Task.Yield();
        }
    }

    private static SourceInfo Describe(FileInfo file) => new()
    {
        Length = file.Length,
        Name = file.Name,
        LastModified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
        Identity = file.FullName
    };
}
