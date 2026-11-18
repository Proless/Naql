using System.Threading;
using System.Threading.Tasks;

namespace Naql;

/// <summary>
/// Keeps the checkpoint of one transfer between runs, so the transfer continues by itself after a cancellation, a
/// failure or a restart of the process.
/// </summary>
/// <remarks>
/// Pass one to <see cref="TransferBuilder.WithCheckpoints"/>. The engine loads it before starting, saves it every
/// <see cref="TransferOptions.CheckpointInterval"/> while the transfer runs and once more when it stops, and deletes it
/// when the transfer completes. The store is best effort: a store that fails, or takes longer than
/// <see cref="TransferOptions.ResponseTimeout"/>, doesn't fail the transfer. If loading fails, the transfer starts over.
/// </remarks>
public interface ICheckpointStore
{
    /// <summary>
    /// Returns the saved checkpoint, or <c>null</c> when there is none or it can't be read.
    /// </summary>
    Task<TransferCheckpoint?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the saved checkpoint.
    /// </summary>
    Task SaveAsync(TransferCheckpoint checkpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the saved checkpoint, if there is one.
    /// </summary>
    Task DeleteAsync(CancellationToken cancellationToken);
}
