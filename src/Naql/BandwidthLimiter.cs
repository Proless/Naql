using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Naql;

/// <summary>
/// Limits the average rate of one or more transfers. Share an instance between transfers to limit their combined rate.
/// </summary>
/// <remarks>
/// A token bucket: up to a tenth of a second's worth of bytes can pass at once, and every byte beyond that waits for
/// its share of time. Callers that go over the limit wait in turn, so parallel parts share the rate fairly.
/// </remarks>
public sealed class BandwidthLimiter
{
    // Below this, waiting costs more than it saves: the debt carries over to the next call instead
    private const double MinWaitSeconds = 0.001;

    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double _burst;
    private double _tokens;
    private TimeSpan _lastRefill;

    /// <summary>
    /// Creates a limiter.
    /// </summary>
    /// <param name="bytesPerSecond">The highest average rate</param>
    public BandwidthLimiter(long bytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        BytesPerSecond = bytesPerSecond;
        _burst = Math.Max(1, bytesPerSecond / 10.0);
        _tokens = _burst;
    }

    /// <summary>
    /// The highest average rate.
    /// </summary>
    public long BytesPerSecond { get; }

    /// <summary>
    /// Accounts for <paramref name="bytes"/> that just moved, and waits as long as needed to stay within the limit.
    /// </summary>
    public Task WaitAsync(long bytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        double waitSeconds;
        lock (_lock)
        {
            var now = _clock.Elapsed;
            _tokens = Math.Min(_burst, _tokens + ((now - _lastRefill).TotalSeconds * BytesPerSecond));
            _lastRefill = now;
            _tokens -= bytes;
            waitSeconds = _tokens < 0 ? -_tokens / BytesPerSecond : 0;
        }

        return waitSeconds >= MinWaitSeconds
            ? Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken)
            : Task.CompletedTask;
    }
}
