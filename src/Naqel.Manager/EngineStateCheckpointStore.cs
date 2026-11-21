using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// Keeps one transfer's checkpoint in the engine state the manager provides, so it survives a restart with the job.
/// </summary>
internal sealed class EngineStateCheckpointStore(IEngineState state, string jobId, string key) : ICheckpointStore
{
    public async Task<TransferCheckpoint?> LoadAsync(CancellationToken cancellationToken)
    {
        var text = await state.GetAsync(jobId, key, cancellationToken).ConfigureAwait(false);
        if (text == null)
        {
            return null;
        }
        try
        {
            return TransferCheckpoint.Deserialize(text);
        }
        catch (FormatException)
        {
            // A checkpoint that can't be read is no help; the transfer starts over
            return null;
        }
    }

    public Task SaveAsync(TransferCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return state.SetAsync(jobId, key, checkpoint.Serialize(), cancellationToken);
    }

    public Task DeleteAsync(CancellationToken cancellationToken) => state.SetAsync(jobId, key, null, cancellationToken);
}
