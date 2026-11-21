using System.Threading;
using System.Threading.Tasks;

namespace Naqel.Storage;

/// <summary>
/// A storage whose items can be found by their ids (<see cref="StorageCapabilities.Ids"/>), also after they moved.
/// </summary>
public interface IStorageLookup
{
    /// <summary>
    /// The item with the id <paramref name="id"/>, or <c>null</c> when there's none.
    /// </summary>
    Task<StorageItem?> GetByIdAsync(string id, CancellationToken cancellationToken);
}
