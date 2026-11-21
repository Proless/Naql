using System.Threading.Channels;
using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// What the manager gives one engine: reports go into the manager's queue, and state goes to the job store.
/// </summary>
internal sealed class EngineHost(ChannelWriter<EngineReport> reports, IJobStore store) : IEngineHost, IEngineState
{
    public IEngineState State => this;

    public void Report(string jobId, JobSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(snapshot);
        // The queue has no bound, so a report never waits; after shutdown it's dropped
        reports.TryWrite(new EngineReport(jobId, snapshot));
    }

    public Task<string?> GetAsync(string jobId, string key, CancellationToken cancellationToken) =>
        store.GetEngineStateAsync(jobId, key, cancellationToken);

    public Task SetAsync(string jobId, string key, string? value, CancellationToken cancellationToken) =>
        store.SetEngineStateAsync(jobId, key, value, cancellationToken);
}

/// <summary>
/// What an engine reported about one of its jobs.
/// </summary>
internal sealed record EngineReport(string JobId, JobSnapshot Snapshot);
