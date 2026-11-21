namespace Naqel.Manager;

/// <summary>
/// A job store in memory: jobs survive a new manager in the same process, but not the process. For tests, and for
/// hosts that don't keep jobs.
/// </summary>
public sealed class MemoryJobStore : IJobStore
{
    private readonly Lock _lock = new();
    private readonly List<StoredJob> _jobs = [];
    private readonly Dictionary<(string JobId, string Key), string> _state = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredJob>> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<StoredJob>>([.. _jobs]);
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(StoredJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_lock)
        {
            var index = _jobs.FindIndex(kept => kept.Job.Id == job.Job.Id);
            if (index >= 0)
            {
                _jobs[index] = job;
            }
            else
            {
                _jobs.Add(job);
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _jobs.RemoveAll(kept => kept.Job.Id == jobId);
            foreach (var key in _state.Keys.Where(key => key.JobId == jobId).ToList())
            {
                _state.Remove(key);
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetEngineStateAsync(string jobId, string key, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_state.GetValueOrDefault((jobId, key)));
        }
    }

    /// <inheritdoc />
    public Task SetEngineStateAsync(string jobId, string key, string? value, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (value == null)
            {
                _state.Remove((jobId, key));
            }
            else
            {
                _state[(jobId, key)] = value;
            }
        }
        return Task.CompletedTask;
    }
}
