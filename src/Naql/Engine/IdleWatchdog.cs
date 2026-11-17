using System;
using System.Diagnostics;
using System.Threading;

namespace Naql.Engine;

/// <summary>
/// Cancels a part's attempt when no bytes move for longer than the idle timeout, and remembers which side stalled.
/// </summary>
internal sealed class IdleWatchdog : IDisposable
{
    private static readonly TimeSpan MaxCheckPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinCheckPeriod = TimeSpan.FromMilliseconds(10);

    private readonly MeteredStream _stream;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _attempt;
    private readonly Stopwatch _clock;
    private readonly Timer _timer;
    private int _timedOut;

    private IdleWatchdog(MeteredStream stream, TimeSpan timeout, CancellationTokenSource attempt, Stopwatch clock)
    {
        _stream = stream;
        _timeout = timeout;
        _attempt = attempt;
        _clock = clock;

        var period = TimeSpan.FromTicks(Math.Clamp(timeout.Ticks / 4, MinCheckPeriod.Ticks, MaxCheckPeriod.Ticks));
        _timer = new Timer(_ => Check(), null, period, period);
    }

    /// <summary>
    /// Whether the attempt was canceled for being idle.
    /// </summary>
    public bool TimedOut => Volatile.Read(ref _timedOut) == 1;

    /// <summary>
    /// The side that stalled: the source when the target was waiting for it, the target otherwise.
    /// </summary>
    public TransferSide StalledSide { get; private set; }

    /// <summary>
    /// Starts watching, or returns <c>null</c> when the timeout is infinite.
    /// </summary>
    public static IdleWatchdog? Start(MeteredStream stream, TimeSpan timeout, CancellationTokenSource attempt, Stopwatch clock)
    {
        return timeout == Timeout.InfiniteTimeSpan ? null : new IdleWatchdog(stream, timeout, attempt, clock);
    }

    public void Dispose() => _timer.Dispose();

    private void Check()
    {
        if (TimedOut || _stream.Throttling || _clock.Elapsed - _stream.LastActivity < _timeout)
        {
            return;
        }

        StalledSide = _stream.ReadPending ? TransferSide.Source : TransferSide.Target;
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
