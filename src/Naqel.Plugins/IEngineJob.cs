namespace Naqel.Plugins;

/// <summary>
/// One job of an engine. A job isn't one transfer: it can have many files, such as a folder or a torrent, and keep
/// running after its content is complete, such as a torrent that seeds.
/// </summary>
public interface IEngineJob
{
    /// <summary>
    /// The engine's own id for the job, which the manager keeps to find it again with
    /// <see cref="ITransferEngine.AttachAsync"/>.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// What the job is doing now. Safe to read from any thread.
    /// </summary>
    JobSnapshot Snapshot { get; }

    /// <summary>
    /// Stops the job so it can continue later where it stopped. Completes once it has stopped.
    /// </summary>
    Task PauseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Continues a paused or failed job.
    /// </summary>
    Task ResumeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops the job for good and forgets it, deleting what it transferred when <paramref name="deleteData"/> is set and
    /// the engine can (<see cref="EngineCapabilities.DeleteData"/>).
    /// </summary>
    Task RemoveAsync(bool deleteData, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the job's own limits, also while it runs.
    /// </summary>
    Task SetLimitsAsync(JobLimits limits, CancellationToken cancellationToken);
}
