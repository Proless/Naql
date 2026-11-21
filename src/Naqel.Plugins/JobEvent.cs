namespace Naqel.Plugins;

/// <summary>
/// Something that happened to a job.
/// </summary>
/// <param name="Kind">What happened</param>
/// <param name="Job">The job right after it happened</param>
/// <param name="Time">When it happened</param>
public sealed record JobEvent(JobEventKind Kind, JobInfo Job, DateTimeOffset Time)
{
    /// <summary>
    /// The state the job had before, for events that change it.
    /// </summary>
    public JobState? PreviousState { get; init; }
}
