using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Naqel.Storage;

/// <summary>
/// Resolves destinations under a root URI into files in a storage: <c>dropbox:/Backups</c> and <c>photos/a.jpg</c>
/// write <c>Backups/photos/a.jpg</c>. Any storage gets its resolving from this class.
/// </summary>
/// <remarks>
/// A single file, which comes with an empty relative path, goes into the destination with its own name when the
/// destination is a folder: when it ends with <c>/</c> or a folder is there. Otherwise the destination is the file.
/// The targets are <see cref="StorageTarget"/>s.
/// </remarks>
public sealed class StorageTargetResolver : ITargetResolver, IStorageLocator
{
    private readonly StorageRoot _root;

    /// <summary>
    /// A resolver for the destinations under <paramref name="root"/>, such as <c>dropbox:/</c>, in
    /// <paramref name="storage"/>, writing with <paramref name="mode"/>.
    /// </summary>
    public StorageTargetResolver(Uri root, IStorage storage, WriteMode mode = WriteMode.Overwrite)
    {
        _root = new StorageRoot(root, storage);
        Mode = mode;
    }

    /// <summary>
    /// The root URI the storage's paths are under.
    /// </summary>
    public Uri Root => _root.Root;

    /// <summary>
    /// The storage.
    /// </summary>
    public IStorage Storage => _root.Storage;

    /// <summary>
    /// What a write does when a file is there already.
    /// </summary>
    public WriteMode Mode { get; }

    /// <inheritdoc />
    public bool CanResolve(Uri destination) => _root.Contains(destination);

    /// <inheritdoc />
    public bool TryLocate(Uri reference, [NotNullWhen(true)] out IStorage? storage, [NotNullWhen(true)] out string? path)
    {
        if (!_root.Contains(reference))
        {
            (storage, path) = (null, null);
            return false;
        }
        (storage, path) = (Storage, _root.PathOf(reference));
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<ITransferTarget> ResolveAsync(Uri destination, string relativePath, SourceInfo? info, ResolveContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var folder = _root.PathOf(destination);
        var segments = RelativePath.Split(relativePath);
        string path;
        if (segments.Count > 0)
        {
            path = RelativePath.Combine(folder, relativePath);
        }
        else if (destination.AbsolutePath.EndsWith('/') || folder.Length == 0 ||
                 (await Storage.GetAsync(folder, cancellationToken).ConfigureAwait(false))?.IsFolder == true)
        {
            var name = info?.Name is { Length: > 0 } known && RelativePath.IsValid(known) && RelativePath.Split(known).Count == 1
                ? known
                : throw new TransferException($"{destination} is a folder, and the content has no name to go into it under",
                    TransferErrorKind.Unsupported, side: TransferSide.Target);
            path = RelativePath.Combine(folder, name);
        }
        else
        {
            path = folder;
        }
        return new StorageTarget(Storage, path, Mode, Storage.OpenWrite(path, Mode, info));
    }
}
