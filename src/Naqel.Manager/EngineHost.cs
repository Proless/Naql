using System.Threading.Channels;
using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// What the manager gives one engine: reports go into the manager's queue, and state goes to the job store.
/// </summary>
internal sealed class EngineHost(ChannelWriter<ManagerMessage> reports, IJobStore store, Interactions interactions) : IEngineHost, IEngineState
{
    public IEngineState State => this;

    public IInteraction InteractionFor(string jobId) => interactions.For(jobId);

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
/// Something for the manager's loop to take in, in order, under its lock.
/// </summary>
internal abstract record ManagerMessage(string JobId);

/// <summary>
/// What an engine reported about one of its jobs.
/// </summary>
internal sealed record EngineReport(string JobId, JobSnapshot Snapshot) : ManagerMessage(JobId);

/// <summary>
/// A question about a job was asked or ended, so whether it needs attention may have changed.
/// </summary>
internal sealed record AttentionChange(string JobId) : ManagerMessage(JobId);
