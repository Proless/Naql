using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Files;

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
