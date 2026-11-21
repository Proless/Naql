using Naqel.Plugins;

namespace Naqel.Manager.Tests.Fakes;

/// <summary>
/// Keeps every event it gets, optionally waiting at a gate or throwing first.
/// </summary>
public sealed class RecordingObserver : IJobObserver
{
    private readonly Lock _lock = new();
    private readonly List<JobEvent> _events = [];

    /// <summary>Every event waits for this before it's handled, if set.</summary>
    public Task? Gate { get; set; }

    /// <summary>Throws instead of handling events, if set.</summary>
    public bool Throws { get; set; }

    public IReadOnlyList<JobEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<JobEventKind> Kinds(string jobId) => [.. Events.Where(e => e.Job.Id == jobId).Select(e => e.Kind)];

    public async Task OnEventAsync(JobEvent jobEvent, CancellationToken cancellationToken)
    {
        if (Gate != null)
        {
            await Gate.WaitAsync(cancellationToken);
        }
        if (Throws)
        {
            throw new InvalidOperationException("The observer broke");
        }
        lock (_lock)
        {
            _events.Add(jobEvent);
        }
    }
}
