namespace Naqel.Plugins;

/// <summary>
/// A job as the manager knows it, at a moment: what was added, where it stands, and the data kept with it.
/// </summary>
public sealed record JobInfo
{
    /// <summary>The manager's id for the job.</summary>
    public required string Id { get; init; }

    /// <summary>What to transfer, as it was added.</summary>
    public required Uri Reference { get; init; }

    /// <summary>Where to put it.</summary>
    public required Uri Destination { get; init; }

    /// <summary>The name of the engine that runs the job (<see cref="ITransferEngine.Name"/>).</summary>
    public required string Engine { get; init; }

    /// <summary>Where the job stands.</summary>
    public JobState State { get; init; } = JobState.Queued;

    /// <summary>What the engine last reported: progress, files, and the error of a failed job.</summary>
    public JobSnapshot Progress { get; init; } = JobSnapshot.Empty;

    /// <summary>The job's own limits.</summary>
    public JobLimits Limits { get; init; } = JobLimits.None;

    /// <summary>Labels the user gave the job, such as <c>work</c>, for grouping and filtering.</summary>
    public IReadOnlySet<string> Labels { get; init; } = new HashSet<string>();

    /// <summary>
    /// Data plugins keep with the job, such as what a completion step did with it. Keys start with the plugin's name,
    /// such as <c>myplugin.moved</c>; keys starting with <c>naqel.</c> belong to Naqel itself.
    /// </summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();

    /// <summary>When the job was added.</summary>
    public DateTimeOffset Added { get; init; }

    /// <summary>When the job completed, or <c>null</c>.</summary>
    public DateTimeOffset? Completed { get; init; }

    /// <summary>Why the job failed, when its state is <see cref="JobState.Failed"/>.</summary>
    public JobError? Error { get; init; }
}
