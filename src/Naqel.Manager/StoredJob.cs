using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// A job as the job store keeps it: what the manager knows about it, and the engine's id for it.
/// </summary>
/// <param name="Job">The job, with its state, progress, labels and data</param>
/// <param name="EngineJobId">The engine's own id for the job, or <c>null</c> before the engine started it</param>
public sealed record StoredJob(JobInfo Job, string? EngineJobId);
