namespace Naqel.Plugins;

/// <summary>
/// What happened to a job.
/// </summary>
public enum JobEventKind
{
    /// <summary>The job was added.</summary>
    Added,

    /// <summary>The job left the queue and its engine started it, the first time or again after a pause.</summary>
    Started,

    /// <summary>The job's state changed, such as to <see cref="JobState.Paused"/> or <see cref="JobState.NeedsAttention"/>.</summary>
    StateChanged,

    /// <summary>The job's content is complete and its completion steps ran.</summary>
    Completed,

    /// <summary>The job failed.</summary>
    Failed,

    /// <summary>The job was removed.</summary>
    Removed,

    /// <summary>The job's labels or data changed.</summary>
    DataChanged
}
