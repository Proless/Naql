using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Naqel.Storage;

/// <summary>
/// A place where files live and can be browsed, such as a local folder, a network share, an SFTP server or a cloud
/// drive: one account on one service. A storage only lists, describes, reads and writes;
/// <see cref="StorageSourceResolver"/> and <see cref="StorageTargetResolver"/> do the resolving for every storage.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Paths are relative to the storage's root, in the form <see cref="RelativePath"/> describes: <c>""</c> for
/// the root, <c>photos/2026/a.jpg</c> for a file.</item>
/// <item>A storage with <see cref="StorageCapabilities.IgnoresCase"/> finds an item whatever case its path is given
/// in, and reports the case it has.</item>
/// <item>Operations a storage may not have, such as deleting or moving, are interfaces of their own:
/// <see cref="IStorageDelete"/>, <see cref="IStorageMove"/>, <see cref="IStorageCopy"/> and
/// <see cref="IStorageLookup"/>.</item>
/// </list>
/// </remarks>
public interface IStorage
{
    /// <summary>
    /// What the storage's paths and items are like.
    /// </summary>
    StorageCapabilities Capabilities { get; }

    /// <summary>
    /// The file or folder at <paramref name="path"/>, or <c>null</c> when there's none. With
    /// <see cref="StorageCapabilities.DuplicateNames"/>, one of the items with that path.
    /// </summary>
    /// <exception cref="TransferException">The storage couldn't be asked</exception>
    Task<StorageItem?> GetAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// The files and folders in the folder <paramref name="folder"/>, and with <paramref name="recursive"/> those of
    /// its subfolders too. Every item's path starts with the folder's path.
    /// </summary>
    /// <exception cref="TransferException">There's no such folder (<see cref="TransferErrorKind.NotFound"/>), or the
    /// storage couldn't be asked</exception>
    IAsyncEnumerable<StorageItem> ListAsync(string folder, bool recursive, CancellationToken cancellationToken);

    /// <summary>
    /// A source that reads <paramref name="file"/>.
    /// </summary>
    ITransferSource OpenRead(StorageItem file);

    /// <summary>
    /// A target that writes the file at <paramref name="path"/>, creating the folders on the way as needed.
    /// </summary>
    /// <param name="path">Where the file goes</param>
    /// <param name="mode">What to do when a file is there already</param>
    /// <param name="info">What's known about the content, such as its length, if anything</param>
    ITransferTarget OpenWrite(string path, WriteMode mode, SourceInfo? info);
}
