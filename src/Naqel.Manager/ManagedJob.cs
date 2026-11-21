using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// A job and what the manager holds for it. Changed only under the manager's lock.
/// </summary>
internal sealed class ManagedJob(JobInfo info, ITransferEngine? engine, string? engineJobId)
{
    /// <summary>What the job is now; replaced, never changed, so it can be handed out.</summary>
    public JobInfo Info { get; set; } = info;

    /// <summary>The engine that runs the job, or <c>null</c> when it isn't available.</summary>
    public ITransferEngine? Engine { get; } = engine;

    /// <summary>The engine's job, once the engine started or reattached it.</summary>
    public IEngineJob? EngineJob { get; set; }

    /// <summary>The engine's id for the job, kept to reattach it after a restart.</summary>
    public string? EngineJobId { get; set; } = engineJobId;

    /// <summary>The state the engine last reported, which the job takes on once its completion steps ran.</summary>
    public JobState EngineState { get; set; } = info.State;

    /// <summary>The last message made before the engine last started the job; the engine's reports up to it are stale.</summary>
    public long StartedAfter { get; set; }

    /// <summary>How many questions about the job wait for the user; while any do, the job needs attention.</summary>
    public int Attention { get; set; }

    /// <summary>Stops the completion steps, when they run.</summary>
    public CancellationTokenSource? Finishing { get; set; }

    public JobRequest Request => new(Info.Id, Info.Reference, Info.Destination) { Limits = Info.Limits };

    public StoredJob Stored => new(Info, EngineJobId);
}
