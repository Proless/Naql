namespace Naqel.Plugins;

/// <summary>
/// A step that runs once a job's content is complete, before the job counts as completed, such as moving the result
/// to where it belongs.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The steps run one after another, in the order they were added, while the job is
/// <see cref="JobState.Finishing"/>. A step that doesn't apply to a job returns at once.</item>
/// <item>A step that throws fails the job, with an error from the exception (<see cref="JobError.From"/>). Resuming
/// the job runs the steps again from the one that failed.</item>
/// <item>A step that finished doesn't run again for the job, also not after a restart; one interrupted by a restart
/// runs again, so a step must cope with having half run.</item>
/// </list>
/// </remarks>
public interface ICompletionStep
{
    /// <summary>
    /// The step's stable name, such as <c>myplugin.move</c>, which the job's data records once the step finished.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Runs the step for a job.
    /// </summary>
    /// <param name="context">The job, and a way to add to its data</param>
    /// <param name="cancellationToken">Canceled when the job is removed or the manager shuts down</param>
    Task RunAsync(ICompletionContext context, CancellationToken cancellationToken);
}
