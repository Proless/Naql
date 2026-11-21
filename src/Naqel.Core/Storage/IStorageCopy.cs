using System.Threading;
using System.Threading.Tasks;

namespace Naqel.Storage;

/// <summary>
/// A storage that can copy files within itself, without moving their bytes through the app, such as a cloud drive's
/// server-side copy.
/// </summary>
public interface IStorageCopy
{
    /// <summary>
    /// Copies the file <paramref name="file"/> to <paramref name="path"/>, creating the folders on the way as needed.
    /// </summary>
    /// <returns>The copy</returns>
    /// <exception cref="TransferException">Something is at <paramref name="path"/> already
    /// (<see cref="TransferErrorKind.AlreadyExists"/>), or the copy failed</exception>
    Task<StorageItem> CopyAsync(StorageItem file, string path, CancellationToken cancellationToken);
}
