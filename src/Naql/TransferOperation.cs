using Naql.Engine;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Naql;

/// <summary>
/// A transfer that is ready to run: a source, a target and its options.
/// </summary>
public sealed class TransferOperation
{
    private readonly BandwidthLimiter? _sharedLimiter;
    private volatile TransferRun? _run;
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

        var limiters = new List<BandwidthLimiter>();
        if (Options.MaxBytesPerSecond > 0)
        {
            limiters.Add(new BandwidthLimiter(Options.MaxBytesPerSecond));
        }
        if (_sharedLimiter != null)
        {
            limiters.Add(_sharedLimiter);
        }

        var run = new TransferRun(Source, Target, Options, limiters.ToArray(), progress, ResumeFrom, CheckpointStore);
        _run = run;
        return run.RunAsync(cancellationToken);
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
