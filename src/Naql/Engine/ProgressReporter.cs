using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Engine;

/// <summary>
/// Reports progress on a timer, away from the transfer's own work, so the caller's code never runs inside a read.
/// </summary>
internal sealed class ProgressReporter
{
    private readonly IProgress<TransferProgress> _progress;
    private readonly PeriodicTimer _timer;
    private readonly Func<TransferProgress> _snapshot;
    private Task _loop = Task.CompletedTask;

    public ProgressReporter(IProgress<TransferProgress> progress, TimeSpan interval, Func<TransferProgress> snapshot)
    {
        _progress = progress;
        _timer = new PeriodicTimer(interval);
        _snapshot = snapshot;
    }

    /// <summary>
    /// Starts reporting every interval.
    /// </summary>
    /// <param name="onFailure">Called when the caller's progress handler throws; reporting stops after that</param>
    public void Start(Action<Exception> onFailure)
    {
        _loop = Task.Run(async () =>
        {
            try
            {
                // Ends with false once the timer is disposed
                while (await _timer.WaitForNextTickAsync().ConfigureAwait(false))
                {
                    _progress.Report(_snapshot());
                }
            }
            catch (Exception ex)
            {
                onFailure(ex);
            }
        });
    }

    /// <summary>
    /// Stops reporting and waits until a report in progress has finished.
    /// </summary>
    public async Task StopAsync()
    {
        _timer.Dispose();
        await _loop.ConfigureAwait(false);
    }

    /// <summary>
    /// Reports once more, after <see cref="StopAsync"/>. An exception from the caller's handler surfaces unchanged.
    /// </summary>
    public void ReportNow() => _progress.Report(_snapshot());
}

/// <summary>
/// Measures the rate over the last few seconds from samples of the transferred bytes.
/// </summary>
internal sealed class RateMeter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private readonly Queue<(TimeSpan Time, long Bytes)> _samples = new();
    private readonly object _lock = new();

    /// <summary>
    /// Adds a sample and returns the bytes per second since the oldest sample in the window.
    /// </summary>
    public double Sample(TimeSpan now, long bytes)
    {
        lock (_lock)
        {
            _samples.Enqueue((now, bytes));
            // Keep one sample older than the window, so the rate always spans the whole window
            while (_samples.Count > 2 && now - _samples.ElementAt(1).Time >= Window)
            {
                _samples.Dequeue();
            }

            var oldest = _samples.Peek();
            var seconds = (now - oldest.Time).TotalSeconds;
            return seconds > 0 ? Math.Max(0, bytes - oldest.Bytes) / seconds : 0;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _samples.Clear();
        }
    }
}
