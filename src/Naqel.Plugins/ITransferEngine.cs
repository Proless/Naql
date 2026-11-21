namespace Naqel.Plugins;

/// <summary>
/// Something that runs jobs: Naqel's own engine, or another one for protocols such as BitTorrent. The manager drives
/// every engine through this contract, so the queue, the limits and the UI work the same for all of them.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>An engine may run in the app, or in another process or container it talks to; every call is asynchronous.</item>
/// <item>The manager calls <see cref="StartAsync"/> once, before anything else.</item>
/// <item>An engine tells the manager about every change to a job through <see cref="IEngineHost.Report"/>.</item>
/// </list>
/// </remarks>
public interface ITransferEngine : IAsyncDisposable
{
    /// <summary>
    /// The engine's stable name, such as <c>naqel</c>; the manager keeps it with each job, to find the engine again
    /// after a restart.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// What the engine can do with its jobs.
    /// </summary>
    EngineCapabilities Capabilities { get; }

    /// <summary>
    /// Whether the engine takes jobs for <paramref name="reference"/>, from a quick look at it such as its scheme,
    /// without a request.
    /// </summary>
    bool CanHandle(Uri reference);

    /// <summary>
    /// Starts the engine, or connects to it, with <paramref name="host"/> for reporting and keeping state.
    /// </summary>
    Task StartAsync(IEngineHost host, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a new job, which begins transferring at once.
    /// </summary>
    /// <exception cref="TransferException">The engine can't take the job</exception>
    Task<IEngineJob> AddAsync(JobRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Finds a job again after a restart, by the id <see cref="IEngineJob.Id"/> gave it. The job comes back paused;
    /// the manager resumes it when its turn comes.
    /// </summary>
    /// <param name="request">The job as it was added, with its current limits</param>
    /// <param name="engineJobId">The engine's own id for the job</param>
    /// <param name="cancellationToken">Stops attaching</param>
    /// <returns>The job, or <c>null</c> when the engine no longer has it</returns>
    Task<IEngineJob?> AttachAsync(JobRequest request, string engineJobId, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the limits for all of the engine's jobs together, also while they run.
    /// </summary>
    Task SetLimitsAsync(EngineLimits limits, CancellationToken cancellationToken);
}
