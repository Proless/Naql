using System.Threading;
using System.Threading.Tasks;

namespace Naqel.Storage;

/// <summary>
/// A storage that can move and rename items within itself, without moving their bytes through the app.
/// </summary>
public interface IStorageMove
{
    /// <summary>
    /// Moves <paramref name="item"/> to <paramref name="path"/>, creating the folders on the way as needed.
    /// </summary>
    /// <returns>The item where it is now</returns>
    /// <exception cref="TransferException">Something is at <paramref name="path"/> already
    /// (<see cref="TransferErrorKind.AlreadyExists"/>), or the move failed</exception>
    Task<StorageItem> MoveAsync(StorageItem item, string path, CancellationToken cancellationToken);
}
