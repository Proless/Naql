using Naql.Engine;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Naql;

/// <summary>
/// A transfer that is ready to run: a source, a target and its options.
/// </summary>
public sealed class TransferOperation
{
    private readonly BandwidthLimiter? _sharedLimiter;
    private readonly BandwidthLimiter _limiter;
    private readonly object _lock = new();
    private volatile TransferRun? _run;
    private int _maxConnections;
    private int _started;

    internal TransferOperation(ITransferSource source, ITransferTarget target, TransferOptions options, BandwidthLimiter? sharedLimiter,
        TransferCheckpoint? resumeFrom = null, ICheckpointStore? checkpointStore = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        ResumeFrom = resumeFrom;
        CheckpointStore = checkpointStore;
        _sharedLimiter = sharedLimiter;
        _limiter = new BandwidthLimiter(options.MaxBytesPerSecond);
        _maxConnections = options.MaxConnections;
    }

    /// <summary>
    /// Where the content is read from.
    /// </summary>
    public ITransferSource Source { get; }

    /// <summary>
    /// Where the content is written to.
    /// </summary>
    public ITransferTarget Target { get; }

    /// <summary>
    /// The settings of this transfer.
    /// </summary>
    public TransferOptions Options { get; }

    /// <summary>
    /// The checkpoint of an earlier run this transfer continues, or <c>null</c>.
    /// </summary>
    public TransferCheckpoint? ResumeFrom { get; }

    /// <summary>
    /// Where the transfer loads and saves its checkpoint by itself, or <c>null</c>.
    /// </summary>
    public ICheckpointStore? CheckpointStore { get; }

    /// <summary>
    /// Runs the transfer. An operation runs only once.
    /// </summary>
    /// <param name="progress">Receives progress every <see cref="TransferOptions.ProgressInterval"/> and once at the end</param>
    /// <param name="cancellationToken">Stops the transfer</param>
    /// <exception cref="TransferException">The transfer failed</exception>
    /// <exception cref="OperationCanceledException">The transfer was canceled</exception>
    /// <exception cref="InvalidOperationException">The operation already ran</exception>
    public Task<TransferResult> RunAsync(IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("A transfer operation runs only once; build a new one to transfer again");
        }

        BandwidthLimiter[] limiters = _sharedLimiter == null ? [_limiter] : [_limiter, _sharedLimiter];
        TransferRun run;
        lock (_lock)
        {
            run = new TransferRun(Source, Target, Options, limiters, progress, ResumeFrom, CheckpointStore);
            run.SetMaxConnections(_maxConnections);
            _run = run;
        }
        return run.RunAsync(cancellationToken);
    }

    /// <summary>
    /// The highest average rate of this transfer in bytes per second, or 0 for no limit. It starts as
    /// <see cref="TransferOptions.MaxBytesPerSecond"/> and changes with <see cref="SetMaxBytesPerSecond"/>.
    /// </summary>
    public long MaxBytesPerSecond => _limiter.BytesPerSecond;

    /// <summary>
    /// How many parts may move at once. It starts as <see cref="TransferOptions.MaxConnections"/> and changes with
    /// <see cref="SetMaxConnections"/>.
    /// </summary>
    public int MaxConnections => Volatile.Read(ref _maxConnections);

    /// <summary>
    /// Changes the highest average rate of this transfer, before or while it runs, such as from a speed setting in an
    /// app. A shared limiter given with <see cref="TransferClient"/> still applies on top.
    /// </summary>
    /// <param name="bytesPerSecond">The highest average rate, or 0 for no limit</param>
    public void SetMaxBytesPerSecond(long bytesPerSecond) => _limiter.BytesPerSecond = bytesPerSecond;

    /// <summary>
    /// Changes how many parts may move at once, before or while the transfer runs. More connections start waiting
    /// parts at once; fewer let the running parts finish and start no new ones until fewer run, so nothing already
    /// transferred is lost. The source's own limit (<see cref="SourceInfo.MaxConnections"/>) still applies. Connections
    /// beyond the planned parts split running parts when the target allows it (<see cref="TargetCapabilities.SplittableParts"/>).
    /// </summary>
    /// <param name="maxConnections">At least 1</param>
    public void SetMaxConnections(int maxConnections)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConnections);
        lock (_lock)
        {
            _maxConnections = maxConnections;
            _run?.SetMaxConnections(maxConnections);
        }
    }

    /// <summary>
    /// What the target has kept so far, to continue the transfer later with <see cref="TransferBuilder.ResumeFrom"/>.
    /// </summary>
    /// <remarks>
    /// Call it after the transfer failed or was canceled, or while it runs, for example from the progress handler, to
    /// save it regularly in case the process ends. It's <c>null</c> before the transfer starts, after it completed,
    /// and when it can't be continued: the target can't resume or discarded its partial data, or the source reports no
    /// version or doesn't support ranges.
    /// </remarks>
    public TransferCheckpoint? GetCheckpoint() => _run?.GetCheckpoint();
}
