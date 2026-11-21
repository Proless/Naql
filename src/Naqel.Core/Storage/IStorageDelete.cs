using System.Threading;
using System.Threading.Tasks;

namespace Naqel.Storage;

/// <summary>
/// A storage that can delete items.
/// </summary>
public interface IStorageDelete
{
    /// <summary>
    /// Deletes <paramref name="item"/>, a folder with everything in it. An item that's gone already isn't an error.
    /// </summary>
    Task DeleteAsync(StorageItem item, CancellationToken cancellationToken);
}
