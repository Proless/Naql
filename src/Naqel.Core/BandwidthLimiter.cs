using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Naqel;

/// <summary>
/// Limits the average rate of one or more transfers. Share an instance between transfers to limit their combined rate.
/// </summary>
/// <remarks>
/// A token bucket: up to a tenth of a second's worth of bytes can pass at once, and every byte beyond that waits for
/// its share of time. Callers that go over the limit wait in turn, so parallel parts share the rate fairly. The rate
/// can change at any time, such as from a speed setting in an app; reads already waiting finish their wait, and the
/// ones after them wait for the new rate.
/// </remarks>
public sealed class BandwidthLimiter
{
    // Below this, waiting costs more than it saves: the debt carries over to the next call instead
    private const double MinWaitSeconds = 0.001;

    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _bytesPerSecond;
    private double _burst;
    private double _tokens;
    private TimeSpan _lastRefill;

    /// <summary>
    /// Creates a limiter.
    /// </summary>
    /// <param name="bytesPerSecond">The highest average rate, or 0 for no limit</param>
    public BandwidthLimiter(long bytesPerSecond)
    {
        BytesPerSecond = bytesPerSecond;
        _tokens = _burst;
    }

    /// <summary>
    /// The highest average rate in bytes per second, or 0 for no limit. Setting it takes effect for the next read.
    /// </summary>
    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSecond);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(BytesPerSecond));
            lock (_lock)
            {
                Refill(_clock.Elapsed);
                _bytesPerSecond = value;
                _burst = Math.Max(1, value / 10.0);
                _tokens = Math.Min(_tokens, _burst);
            }
        }
    }

    /// <summary>
    /// Waits until <paramref name="bytes"/> may pass under the limit.
    /// </summary>
    public Task WaitAsync(long bytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        double waitSeconds;
        lock (_lock)
        {
            if (_bytesPerSecond == 0)
            {
                return Task.CompletedTask;
            }
            Refill(_clock.Elapsed);
            _tokens -= bytes;
            waitSeconds = _tokens < 0 ? -_tokens / _bytesPerSecond : 0;
        }

        return waitSeconds >= MinWaitSeconds
            ? Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken)
            : Task.CompletedTask;
    }

    private void Refill(TimeSpan now)
    {
        _tokens = Math.Min(_burst, _tokens + ((now - _lastRefill).TotalSeconds * _bytesPerSecond));
        _lastRefill = now;
    }
}
