namespace Naqel.Plugins;

/// <summary>
/// Follows what happens to jobs: added, started, a new state, completed, failed, removed, new data.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each observer gets the events in the order they happened, one at a time.</item>
/// <item>Observers run apart from the jobs' own work: a slow observer makes its own events wait, never a job, and an
/// observer that throws doesn't fail anything; the manager reports the failure and goes on with the next event.</item>
/// <item>Progress isn't an event; read it from <see cref="JobInfo.Progress"/> when it matters.</item>
/// </list>
/// </remarks>
public interface IJobObserver
{
    /// <summary>
    /// Handles one event.
    /// </summary>
    /// <param name="jobEvent">What happened</param>
    /// <param name="cancellationToken">Canceled when the manager shuts down</param>
    Task OnEventAsync(JobEvent jobEvent, CancellationToken cancellationToken);
}
