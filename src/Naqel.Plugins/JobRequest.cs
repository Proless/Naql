namespace Naqel.Plugins;

/// <summary>
/// A job for an engine to start.
/// </summary>
/// <param name="JobId">The manager's id for the job, which the engine's state for it is kept under</param>
/// <param name="Reference">What to transfer, such as a link, a <c>magnet:</c> link or a folder</param>
/// <param name="Destination">Where to put it, such as a folder</param>
public sealed record JobRequest(string JobId, Uri Reference, Uri Destination)
{
    /// <summary>The job's own limits.</summary>
    public JobLimits Limits { get; init; } = JobLimits.None;
}
