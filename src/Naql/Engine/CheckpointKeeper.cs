using System;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Engine;

/// <summary>
/// Saves a transfer's checkpoint to an <see cref="ICheckpointStore"/> on a timer, and settles it when the transfer ends.
/// </summary>
/// <remarks>
/// Saving is best effort: a failing store must not fail a transfer that is otherwise fine. The last failure is kept
/// for diagnostics.
/// </remarks>
internal sealed class CheckpointKeeper
{
    private readonly ICheckpointStore _store;
    private readonly PeriodicTimer _timer;
    private readonly Func<TransferCheckpoint?> _take;
    private readonly SemaphoreSlim _saving = new(1, 1);
    private Task _loop = Task.CompletedTask;
    private (long Completed, string? Token, string? Version)? _saved;

    public CheckpointKeeper(ICheckpointStore store, TimeSpan interval, Func<TransferCheckpoint?> take)
    {
        _store = store;
        _timer = new PeriodicTimer(interval);
        _take = take;
    }

    /// <summary>
    /// The last error the store failed with, or <c>null</c>.
    /// </summary>
    public Exception? LastError { get; private set; }

    /// <summary>
    /// Starts saving every interval, whenever the checkpoint changed.
    /// </summary>
    public void Start()
    {
        _loop = Task.Run(async () =>
        {
            // Ends with false once the timer is disposed
            while (await _timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                if (_take() is { } checkpoint)
                {
                    await SaveAsync(checkpoint).ConfigureAwait(false);
                }
            }
        });
    }

    /// <summary>
    /// Stops saving, then deletes the checkpoint of a completed transfer, or saves the final checkpoint of an
    /// unfinished one, or deletes it when the unfinished transfer can't be continued.
    /// </summary>
    public async Task FinishAsync(bool completed)
    {
        _timer.Dispose();
        await _loop.ConfigureAwait(false);

        var checkpoint = completed ? null : _take();
        if (checkpoint != null)
        {
            await SaveAsync(checkpoint).ConfigureAwait(false);
            return;
        }

        try
        {
            await _store.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A checkpoint left behind only makes a later run check the source and start over
            LastError = ex;
        }
    }

    private async Task SaveAsync(TransferCheckpoint checkpoint)
    {
        var state = (checkpoint.Completed, checkpoint.ResumeToken, checkpoint.SourceVersion);
        await _saving.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_saved == state)
            {
                return;
            }
            await _store.SaveAsync(checkpoint, CancellationToken.None).ConfigureAwait(false);
            _saved = state;
        }
        catch (Exception ex)
        {
            // The next tick or the final save tries again
            LastError = ex;
        }
        finally
        {
            _saving.Release();
        }
    }
}
