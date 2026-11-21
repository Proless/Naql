namespace Naqel.Manager;

/// <summary>
/// Where the manager keeps its jobs and the engines' state for them, so they survive a restart.
/// </summary>
/// <remarks>
/// The manager saves a job when its state, limits, labels or data change, not on every bit of progress. Calls can
/// come from several threads at once.
/// </remarks>
public interface IJobStore
{
    /// <summary>
    /// Every job kept, in the order they were added.
    /// </summary>
    Task<IReadOnlyList<StoredJob>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Keeps <paramref name="job"/>, replacing what was kept for it before.
    /// </summary>
    Task SaveAsync(StoredJob job, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets a job and the engine's state for it.
    /// </summary>
    Task DeleteAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>
    /// The engine's value under <paramref name="key"/> for a job, or <c>null</c>.
    /// </summary>
    Task<string?> GetEngineStateAsync(string jobId, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Keeps the engine's value under <paramref name="key"/> for a job, or deletes it when it's <c>null</c>.
    /// </summary>
    Task SetEngineStateAsync(string jobId, string key, string? value, CancellationToken cancellationToken);
}
