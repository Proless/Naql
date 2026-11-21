namespace Naqel.Plugins;

/// <summary>
/// What an engine says about one of its jobs at a moment.
/// </summary>
public sealed record JobSnapshot
{
    /// <summary>
    /// A job that hasn't started yet.
    /// </summary>
    public static JobSnapshot Empty { get; } = new();

    /// <summary>Where the job stands.</summary>
    public JobState State { get; init; } = JobState.Preparing;

    /// <summary>The bytes moved so far, across all files.</summary>
    public long Transferred { get; init; }

    /// <summary>The bytes of all files, or <c>null</c> while any length is unknown.</summary>
    public long? Total { get; init; }

    /// <summary>The current rate in bytes per second.</summary>
    public double Rate { get; init; }

    /// <summary>
    /// Whether every file is complete. It can be <c>true</c> while the job still runs, such as a torrent that seeds.
    /// </summary>
    public bool ContentComplete { get; init; }

    /// <summary>The job's files, as far as they're known.</summary>
    public IReadOnlyList<JobFile> Files { get; init; } = [];

    /// <summary>Why the job failed, when its state is <see cref="JobState.Failed"/>.</summary>
    public JobError? Error { get; init; }
}
