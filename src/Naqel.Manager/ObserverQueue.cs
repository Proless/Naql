using System.Threading.Channels;
using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// Hands one observer its events, in order and one at a time, apart from the jobs and the other observers.
/// </summary>
internal sealed class ObserverQueue
{
    private readonly Channel<JobEvent> _events = Channel.CreateUnbounded<JobEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly IJobObserver _observer;
    private readonly Action<IJobObserver, JobEvent, Exception> _failed;
    private readonly Task _loop;

    public ObserverQueue(IJobObserver observer, Action<IJobObserver, JobEvent, Exception> failed, CancellationToken shutdown)
    {
        _observer = observer;
        _failed = failed;
        _loop = Task.Run(() => RunAsync(shutdown), CancellationToken.None);
    }

    public void Post(JobEvent jobEvent) => _events.Writer.TryWrite(jobEvent);

    /// <summary>
    /// Lets the observer finish the events it already has, unless the manager's shutdown was canceled.
    /// </summary>
    public Task StopAsync()
    {
        _events.Writer.TryComplete();
        return _loop;
    }

    private async Task RunAsync(CancellationToken shutdown)
    {
        try
        {
            await foreach (var jobEvent in _events.Reader.ReadAllAsync(shutdown).ConfigureAwait(false))
            {
                try
                {
                    await _observer.OnEventAsync(jobEvent, shutdown).ConfigureAwait(false);
                }
                catch (Exception ex) when (!shutdown.IsCancellationRequested)
                {
                    // An observer's failure is its own; the next event still comes
                    _failed(_observer, jobEvent, ex);
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Shutting down
        }
    }
}
