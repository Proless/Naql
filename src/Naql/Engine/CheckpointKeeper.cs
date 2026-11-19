using System;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Engine;

/// <summary>
/// Loads a transfer's checkpoint from an <see cref="ICheckpointStore"/>, saves it on a timer, and settles it when the
/// transfer ends.
/// </summary>
/// <remarks>
/// The store is best effort: a store that fails, or takes longer than the timeout, must not fail a transfer that is
/// otherwise fine. A checkpoint that can't be loaded means starting over; one that can't be saved means a later run
/// may have to start over. The last failure is kept for diagnostics.
/// </remarks>
internal sealed class CheckpointKeeper : IDisposable
{
    private readonly ICheckpointStore _store;
    private readonly PeriodicTimer _timer;
    private readonly Func<TransferCheckpoint?> _take;
    private readonly SemaphoreSlim _saving = new(1, 1);
    private readonly TimeSpan _timeout;
    private Task _loop = Task.CompletedTask;
    private (long Completed, string? Token, string? Version)? _saved;

    /// <param name="store">Where the checkpoint is kept</param>
    /// <param name="interval">How often to save it</param>
    /// <param name="timeout">How long each call to the store may take</param>
    /// <param name="take">Returns the current checkpoint, or <c>null</c> when there is nothing to continue</param>
    public CheckpointKeeper(ICheckpointStore store, TimeSpan interval, TimeSpan timeout, Func<TransferCheckpoint?> take)
    {
        _store = store;
        _timeout = timeout;
        _timer = new PeriodicTimer(interval);
        _take = take;
    }

    /// <summary>
    /// The last error the store failed with, or <c>null</c>.
    /// </summary>
    public Exception? LastError { get; private set; }

    /// <summary>
    /// Loads the stored checkpoint, or returns <c>null</c> when there is none or the store failed.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled</exception>
    public async Task<TransferCheckpoint?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await CallStoreAsync(t => _store.LoadAsync(t), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LastError = ex;
            return null;
        }
    }

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
            await CallStoreAsync(async t => { await _store.DeleteAsync(t).ConfigureAwait(false); return true; }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A checkpoint left behind only makes a later run check the source and start over
            LastError = ex;
        }
    }

    /// <summary>
    /// Calls the store, stopping to wait for it after the timeout even if it ignores cancellation.
    /// </summary>
    private async Task<T> CallStoreAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_timeout != Timeout.InfiniteTimeSpan)
        {
            timeout.CancelAfter(_timeout);
        }
        try
        {
            return await call(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The checkpoint store didn't respond within {_timeout.TotalSeconds:0.###} seconds", ex);
        }
    }

    /// <summary>
    /// Stops the timer, if <see cref="FinishAsync"/> hasn't, and releases the lock around saving.
    /// </summary>
    public void Dispose()
    {
        _timer.Dispose();
        _saving.Dispose();
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
            await CallStoreAsync(async t => { await _store.SaveAsync(checkpoint, t).ConfigureAwait(false); return true; }, CancellationToken.None).ConfigureAwait(false);
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
