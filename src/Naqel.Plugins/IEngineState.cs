namespace Naqel.Plugins;

/// <summary>
/// Text an engine keeps for each of its jobs, which survives a restart of the app.
/// </summary>
public interface IEngineState
{
    /// <summary>
    /// The value kept under <paramref name="key"/> for the job, or <c>null</c>.
    /// </summary>
    /// <param name="jobId">The manager's id for the job</param>
    /// <param name="key">What the value is, such as <c>checkpoint</c></param>
    /// <param name="cancellationToken">Stops reading</param>
    Task<string?> GetAsync(string jobId, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Keeps <paramref name="value"/> under <paramref name="key"/> for the job, or deletes it when it's <c>null</c>.
    /// </summary>
    /// <param name="jobId">The manager's id for the job</param>
    /// <param name="key">What the value is, such as <c>checkpoint</c></param>
    /// <param name="value">The value, or <c>null</c> to delete it</param>
    /// <param name="cancellationToken">Stops writing</param>
    Task SetAsync(string jobId, string key, string? value, CancellationToken cancellationToken);
}
