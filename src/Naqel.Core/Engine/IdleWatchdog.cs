using System;
using System.Diagnostics;
using System.Threading;

namespace Naqel.Engine;

/// <summary>
/// Cancels a part's attempt when no bytes move for longer than the idle timeout, and remembers which side stalled.
/// Once the target has read the whole part, it's waiting to finish it, for example for an upload's answer: from then
/// on the response timeout applies instead.
/// </summary>
internal sealed class IdleWatchdog : IDisposable
{
    private static readonly TimeSpan MaxCheckPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinCheckPeriod = TimeSpan.FromMilliseconds(10);

    private readonly MeteredStream _stream;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _responseTimeout;
    private readonly CancellationTokenSource _attempt;
    private readonly Stopwatch _clock;
    private readonly Timer _timer;
    private int _timedOut;

    private IdleWatchdog(MeteredStream stream, TimeSpan idleTimeout, TimeSpan responseTimeout, CancellationTokenSource attempt, Stopwatch clock)
    {
        _stream = stream;
        _idleTimeout = idleTimeout;
        _responseTimeout = responseTimeout;
        _attempt = attempt;
        _clock = clock;

        var shortest = idleTimeout == Timeout.InfiniteTimeSpan ? responseTimeout
            : responseTimeout == Timeout.InfiniteTimeSpan ? idleTimeout
            : TimeSpan.FromTicks(Math.Min(idleTimeout.Ticks, responseTimeout.Ticks));
        var period = TimeSpan.FromTicks(Math.Clamp(shortest.Ticks / 4, MinCheckPeriod.Ticks, MaxCheckPeriod.Ticks));
        _timer = new Timer(_ => Check(), null, period, period);
    }

    /// <summary>
    /// Whether the attempt was canceled for being idle, or for waiting too long for the target to finish.
    /// </summary>
    public bool TimedOut => Volatile.Read(ref _timedOut) == 1;

    /// <summary>
    /// Whether the time out came while the target was finishing a part it had read completely.
    /// </summary>
    public bool WhileFinishing { get; private set; }

    /// <summary>
    /// The side that stalled: the source when the target was waiting for it, the target otherwise.
    /// </summary>
    public TransferSide StalledSide { get; private set; }

    /// <summary>
    /// Starts watching, or returns <c>null</c> when both timeouts are infinite.
    /// </summary>
    public static IdleWatchdog? Start(MeteredStream stream, TimeSpan idleTimeout, TimeSpan responseTimeout, CancellationTokenSource attempt, Stopwatch clock)
    {
        return idleTimeout == Timeout.InfiniteTimeSpan && responseTimeout == Timeout.InfiniteTimeSpan
            ? null
            : new IdleWatchdog(stream, idleTimeout, responseTimeout, attempt, clock);
    }

    public void Dispose() => _timer.Dispose();

    private void Check()
    {
        if (TimedOut)
        {
            return;
        }

        if (_stream.ReachedEnd)
        {
            if (_responseTimeout == Timeout.InfiniteTimeSpan || _clock.Elapsed - _stream.EndedAt < _responseTimeout)
            {
                return;
            }
            WhileFinishing = true;
            StalledSide = TransferSide.Target;
        }
        else
        {
            if (_idleTimeout == Timeout.InfiniteTimeSpan || _stream.Throttling || _clock.Elapsed - _stream.LastActivity < _idleTimeout)
            {
                return;
            }
            StalledSide = _stream.ReadPending ? TransferSide.Source : TransferSide.Target;
        }

        Volatile.Write(ref _timedOut, 1);
        try
        {
            _attempt.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The attempt ended while the timer fired
        }
    }
}
