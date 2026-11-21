using System.Collections.Concurrent;
using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// Naqel's own engine: a job resolves its reference with the source resolvers into one or more files, and moves each
/// to the target the destination resolves to, as a <see cref="TransferOperation"/>, with everything the transfer engine
/// does. It handles every reference its resolvers handle, so it goes after engines for particular protocols.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A job moves its files one after another; each file uses parallel parts as the transfer options allow.</item>
/// <item>What a job needs to continue lives in the manager's engine state: which files are done, and the current
/// file's checkpoint. Pausing, a failure or a restart therefore continue where the job stopped.</item>
/// <item>The engine's limit is one <see cref="BandwidthLimiter"/> all its jobs share; a job's own limits apply to its
/// current transfer on top.</item>
/// <item>Removing a job with its data discards what an unfinished file's target kept, through the target's own abort,
/// and deletes finished files in storages that can delete them (<see cref="Storage.IStorageDelete"/>). Finished files
/// elsewhere, such as in a folder given by a plain <c>file:</c> destination, stay.</item>
/// </list>
/// </remarks>
public sealed class NaqelEngine : ITransferEngine
{
    /// <summary>
    /// The engine's name, which the manager keeps with its jobs.
    /// </summary>
    public const string EngineName = "naqel";

    private readonly ConcurrentDictionary<string, NaqelJob> _jobs = new();
    private readonly BandwidthLimiter _limiter = new(0);
    private IEngineHost? _host;

    /// <summary>
    /// An engine with the resolvers and transfer settings in <paramref name="options"/>.
    /// </summary>
    public NaqelEngine(NaqelEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SourceResolvers = [.. options.SourceResolvers];
        TargetResolvers = [.. options.TargetResolvers];
        TransferOptions = options.TransferOptions ?? throw new ArgumentException("The engine needs transfer options", nameof(options));
        Client = new TransferClient(TransferOptions, _limiter);
    }

    /// <inheritdoc />
    public string Name => EngineName;

    /// <inheritdoc />
    public EngineCapabilities Capabilities { get; } = new() { Pause = true, JobLimits = true, EngineLimits = true, DeleteData = true };

    internal IReadOnlyList<ISourceResolver> SourceResolvers { get; }

    internal IReadOnlyList<ITargetResolver> TargetResolvers { get; }

    internal TransferOptions TransferOptions { get; }

    internal TransferClient Client { get; }

    internal IEngineHost Host => _host ?? throw new InvalidOperationException("The engine hasn't started");

    /// <inheritdoc />
    public bool CanHandle(Uri reference) => reference != null && SourceResolvers.Any(resolver => resolver.CanResolve(reference));

    /// <inheritdoc />
    public Task StartAsync(IEngineHost host, CancellationToken cancellationToken)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IEngineJob> AddAsync(JobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var job = Register(request);
        job.Start();
        return Task.FromResult<IEngineJob>(job);
    }

    /// <inheritdoc />
    /// <remarks>Every job can be found again: what it needs to continue is in the manager's engine state.</remarks>
    public Task<IEngineJob?> AttachAsync(JobRequest request, string engineJobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult<IEngineJob?>(engineJobId == request.JobId ? Register(request) : null);
    }

    /// <inheritdoc />
    public Task SetLimitsAsync(EngineLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limiter.BytesPerSecond = limits.MaxBytesPerSecond;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops every job, keeping what they need to continue.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var job in _jobs.Values)
        {
            await job.StopAsync().ConfigureAwait(false);
        }
        _jobs.Clear();
    }

    internal void Forget(NaqelJob job) => _jobs.TryRemove(new KeyValuePair<string, NaqelJob>(job.Id, job));

    private NaqelJob Register(JobRequest request)
    {
        var job = new NaqelJob(this, request);
        _jobs[request.JobId] = job;
        return job;
    }
}
