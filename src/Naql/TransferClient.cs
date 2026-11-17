using System;
using System.Threading;
using System.Threading.Tasks;

namespace Naql;

/// <summary>
/// Runs transfers with shared defaults and, optionally, a speed limit shared by all of them.
/// Register one for dependency injection, or use <see cref="Transfer"/> for <see cref="Default"/>.
/// </summary>
public sealed class TransferClient
{
    /// <summary>
    /// A client with <see cref="TransferOptions.Default"/> and no shared limit.
    /// </summary>
    public static TransferClient Default { get; } = new();

    /// <summary>
    /// Creates a client.
    /// </summary>
    /// <param name="defaultOptions">The options every transfer starts from</param>
    /// <param name="sharedLimiter">Limits the combined rate of all transfers of this client</param>
    public TransferClient(TransferOptions? defaultOptions = null, BandwidthLimiter? sharedLimiter = null)
    {
        DefaultOptions = defaultOptions ?? TransferOptions.Default;
        SharedLimiter = sharedLimiter;
    }

    /// <summary>
    /// The options every transfer starts from.
    /// </summary>
    public TransferOptions DefaultOptions { get; }

    /// <summary>
    /// Limits the combined rate of all transfers of this client, or <c>null</c>.
    /// </summary>
    public BandwidthLimiter? SharedLimiter { get; }

    /// <summary>
    /// Starts describing a transfer from <paramref name="source"/>.
    /// </summary>
    public TransferBuilder From(ITransferSource source)
    {
        return new TransferBuilder(this, source ?? throw new ArgumentNullException(nameof(source)));
    }

    /// <summary>
    /// Transfers the content of <paramref name="source"/> to <paramref name="target"/>.
    /// </summary>
    /// <exception cref="TransferException">The transfer failed</exception>
    /// <exception cref="OperationCanceledException">The transfer was canceled</exception>
    public Task<TransferResult> RunAsync(ITransferSource source, ITransferTarget target, TransferOptions? options = null,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return new TransferOperation(source, target, options ?? DefaultOptions, SharedLimiter).RunAsync(progress, cancellationToken);
    }
}

/// <summary>
/// The shortest way to run a transfer: <c>Transfer.From(source).To(target).RunAsync()</c>.
/// </summary>
public static class Transfer
{
    /// <summary>
    /// Starts describing a transfer from <paramref name="source"/>, using <see cref="TransferClient.Default"/>.
    /// </summary>
    public static TransferBuilder From(ITransferSource source) => TransferClient.Default.From(source);
}

/// <summary>
/// Describes a transfer step by step.
/// </summary>
public sealed class TransferBuilder
{
    private readonly TransferClient _client;
    private readonly ITransferSource _source;
    private ITransferTarget? _target;
    private TransferOptions _options;
    private TransferCheckpoint? _resumeFrom;
    private ICheckpointStore? _checkpointStore;

    internal TransferBuilder(TransferClient client, ITransferSource source)
    {
        _client = client;
        _source = source;
        _options = client.DefaultOptions;
    }

    /// <summary>
    /// Sets where the content is written to.
    /// </summary>
    public TransferBuilder To(ITransferTarget target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        return this;
    }

    /// <summary>
    /// Replaces all options.
    /// </summary>
    public TransferBuilder WithOptions(TransferOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>
    /// Sets <see cref="TransferOptions.MaxConnections"/>.
    /// </summary>
    public TransferBuilder WithMaxConnections(int value)
    {
        _options = _options with { MaxConnections = value };
        return this;
    }

    /// <summary>
    /// Sets <see cref="TransferOptions.MaxBytesPerSecond"/>. 0 means unlimited.
    /// </summary>
    public TransferBuilder WithMaxRate(long bytesPerSecond)
    {
        _options = _options with { MaxBytesPerSecond = bytesPerSecond };
        return this;
    }

    /// <summary>
    /// Sets <see cref="TransferOptions.Retry"/>.
    /// </summary>
    public TransferBuilder WithRetry(RetryPolicy policy)
    {
        _options = _options with { Retry = policy ?? throw new ArgumentNullException(nameof(policy)) };
        return this;
    }

    /// <summary>
    /// Sets <see cref="TransferOptions.ExpectedChecksum"/>: the transfer fails and discards what it wrote unless the
    /// content has this hash.
    /// </summary>
    public TransferBuilder WithChecksum(Checksum checksum)
    {
        _options = _options with { ExpectedChecksum = checksum ?? throw new ArgumentNullException(nameof(checksum)) };
        return this;
    }

    /// <summary>
    /// Continues the transfer an earlier run left unfinished, using the same kind of source and target. When the
    /// content changed since, or the target lost its partial data, the transfer starts over instead.
    /// </summary>
    public TransferBuilder ResumeFrom(TransferCheckpoint checkpoint)
    {
        _resumeFrom = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
        return this;
    }

    /// <summary>
    /// Lets the transfer keep its own checkpoint in <paramref name="store"/>: it continues from a saved checkpoint,
    /// saves it while running and when it stops, and deletes it once complete. Running the same transfer again with
    /// the same store therefore continues where the last run stopped, even after the process restarted.
    /// </summary>
    /// <remarks>
    /// A checkpoint passed to <see cref="ResumeFrom"/> wins over the stored one. For a file,
    /// <c>FileCheckpointStore.For(path)</c> keeps the checkpoint next to it.
    /// </remarks>
    public TransferBuilder WithCheckpoints(ICheckpointStore store)
    {
        _checkpointStore = store ?? throw new ArgumentNullException(nameof(store));
        return this;
    }

    /// <summary>
    /// Creates the transfer without running it.
    /// </summary>
    /// <exception cref="InvalidOperationException">No target was set</exception>
    public TransferOperation Build()
    {
        var target = _target ?? throw new InvalidOperationException("Set the target with To(...) before building the transfer");
        return new TransferOperation(_source, target, _options, _client.SharedLimiter, _resumeFrom, _checkpointStore);
    }

    /// <summary>
    /// Creates the transfer and runs it.
    /// </summary>
    /// <exception cref="TransferException">The transfer failed</exception>
    /// <exception cref="OperationCanceledException">The transfer was canceled</exception>
    public Task<TransferResult> RunAsync(IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return Build().RunAsync(progress, cancellationToken);
    }
}
