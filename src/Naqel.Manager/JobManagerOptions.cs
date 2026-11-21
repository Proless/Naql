using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// What a <see cref="JobManager"/> works with.
/// </summary>
public sealed class JobManagerOptions
{
    /// <summary>
    /// The engines, in order: a new job goes to the first that handles its reference, so engines for particular
    /// protocols come before Naqel's own engine, which handles anything its resolvers do.
    /// </summary>
    public IList<ITransferEngine> Engines { get; } = new List<ITransferEngine>();

    /// <summary>
    /// What follows the jobs' events.
    /// </summary>
    public IList<IJobObserver> Observers { get; } = new List<IJobObserver>();

    /// <summary>
    /// The steps that run when a job's content is complete, in order.
    /// </summary>
    public IList<ICompletionStep> CompletionSteps { get; } = new List<ICompletionStep>();

    /// <summary>
    /// Where jobs are kept; in memory by default.
    /// </summary>
    public IJobStore Store { get; set; } = new MemoryJobStore();

    /// <summary>
    /// How many jobs may transfer at once; the others wait in the queue. Jobs that are seeding or finishing don't count.
    /// </summary>
    public int MaxActiveJobs
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 3;

    /// <summary>
    /// The clock for the jobs' times.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
