#if !NET6_0_OR_GREATER
using System.Threading.Tasks;

namespace System.Threading;

/// <summary>
/// The part of .NET 6's <c>PeriodicTimer</c> the engine uses, for netstandard2.1. It waits a full period after each
/// wait instead of keeping a fixed rate, which only matters when the work between ticks is slow.
/// </summary>
internal sealed class PeriodicTimer : IDisposable
{
    private readonly TimeSpan _period;
    private readonly CancellationTokenSource _disposed = new();

    public PeriodicTimer(TimeSpan period)
    {
        _period = period;
    }

    /// <summary>
    /// Waits for the next tick; returns <c>false</c> once the timer is disposed, also while waiting.
    /// </summary>
    public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed.IsCancellationRequested)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token, cancellationToken);
        try
        {
            await Task.Delay(_period, linked.Token).ConfigureAwait(false);
            return !_disposed.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    // The source isn't disposed, so a wait that is just starting can still read it
    public void Dispose() => _disposed.Cancel();
}
#endif
