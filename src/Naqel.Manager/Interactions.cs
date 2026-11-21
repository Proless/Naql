using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// The questions plugins and engines ask the user, waiting until a UI or the API answers them: the desktop app answers
/// with a dialog as soon as <see cref="Asked"/> is raised, the server lists <see cref="Pending"/> until the user comes.
/// </summary>
/// <remarks>
/// A question about a job makes the job need attention until it's answered. Questions live in memory: after a restart,
/// a job asks again when it gets to that point again.
/// </remarks>
public sealed class Interactions
{
    private readonly Lock _lock = new();
    private readonly List<Waiting> _pending = [];
    private readonly TimeProvider _time;
    private readonly Action<string>? _jobChanged;

    internal Interactions(TimeProvider time, Action<string>? jobChanged)
    {
        _time = time;
        _jobChanged = jobChanged;
        General = new Asker(this, jobId: null);
    }

    /// <summary>
    /// Raised outside any lock when a question is asked.
    /// </summary>
    public event Action<PendingInteraction>? Asked;

    /// <summary>
    /// Raised outside any lock when a question was answered, declined, or isn't waited for any more.
    /// </summary>
    public event Action<PendingInteraction>? Ended;

    /// <summary>
    /// Asks the user about nothing in particular, such as while signing in to an account.
    /// </summary>
    public IInteraction General { get; }

    /// <summary>
    /// The questions waiting for an answer, in the order they were asked.
    /// </summary>
    public IReadOnlyList<PendingInteraction> Pending
    {
        get
        {
            lock (_lock)
            {
                return [.. _pending.Select(waiting => waiting.Pending)];
            }
        }
    }

    /// <summary>
    /// Asks the user about the job <paramref name="jobId"/>, which needs attention until the user answers.
    /// </summary>
    public IInteraction For(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return new Asker(this, jobId);
    }

    /// <summary>
    /// Answers a waiting question.
    /// </summary>
    /// <returns>Whether the question was still waiting</returns>
    public bool Answer(string id, InteractionAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return End(id, waiting => waiting.Answer.TrySetResult(answer));
    }

    internal int CountFor(string jobId)
    {
        lock (_lock)
        {
            return _pending.Count(waiting => waiting.Pending.Request.JobId == jobId);
        }
    }

    /// <summary>
    /// Stops waiting for the questions about a job, such as when it's removed.
    /// </summary>
    internal void CancelFor(string jobId)
    {
        foreach (var waiting in Pending.Where(pending => pending.Request.JobId == jobId))
        {
            End(waiting.Id, entry => entry.Answer.TrySetCanceled());
        }
    }

    private async Task<InteractionAnswer> AskAsync(InteractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var waiting = new Waiting(new PendingInteraction(Guid.NewGuid().ToString("N"), request, _time.GetUtcNow()));
        lock (_lock)
        {
            _pending.Add(waiting);
        }
        Asked?.Invoke(waiting.Pending);
        if (request.JobId != null)
        {
            _jobChanged?.Invoke(request.JobId);
        }

        using (cancellationToken.Register(() => End(waiting.Pending.Id, entry => entry.Answer.TrySetCanceled(cancellationToken))))
        {
            return await waiting.Answer.Task.ConfigureAwait(false);
        }
    }

    private bool End(string id, Action<Waiting> complete)
    {
        Waiting? waiting;
        lock (_lock)
        {
            waiting = _pending.Find(entry => entry.Pending.Id == id);
            if (waiting == null)
            {
                return false;
            }
            _pending.Remove(waiting);
        }
        complete(waiting);
        Ended?.Invoke(waiting.Pending);
        if (waiting.Pending.Request.JobId is { } jobId)
        {
            _jobChanged?.Invoke(jobId);
        }
        return true;
    }

    private sealed class Waiting(PendingInteraction pending)
    {
        public PendingInteraction Pending { get; } = pending;

        public TaskCompletionSource<InteractionAnswer> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Asks on behalf of one job, or of none
    private sealed class Asker(Interactions interactions, string? jobId) : IInteraction
    {
        public Task<InteractionAnswer> AskAsync(InteractionRequest request, CancellationToken cancellationToken) =>
            interactions.AskAsync(jobId == null ? request : request with { JobId = jobId }, cancellationToken);
    }
}
