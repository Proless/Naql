namespace Naqel.Plugins;

/// <summary>
/// What the manager offers an engine: a way to report changes, and a place to keep state.
/// </summary>
public interface IEngineHost
{
    /// <summary>
    /// Tells the manager what a job is doing now: its state, its progress, its files. Call it from any thread and as
    /// often as something changes; the manager keeps the latest, and only the job's state changes are kept for good.
    /// </summary>
    /// <param name="jobId">The manager's id for the job, from <see cref="JobRequest.JobId"/></param>
    /// <param name="snapshot">What the job is doing now</param>
    void Report(string jobId, JobSnapshot snapshot);

    /// <summary>
    /// Where the engine keeps what it needs to continue its jobs after a restart, such as checkpoints, for an engine
    /// that has no store of its own. The manager deletes a job's state when the job is removed.
    /// </summary>
    IEngineState State { get; }

    /// <summary>
    /// Asks the user about a job, such as for a password its link needs; the job needs attention
    /// (<see cref="JobState.NeedsAttention"/>) until the user answers.
    /// </summary>
    /// <param name="jobId">The manager's id for the job</param>
    IInteraction InteractionFor(string jobId);
}
