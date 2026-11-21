using System.Text.Json;
using Naqel.Plugins;
using Naqel.Storage;

namespace Naqel.Manager;

/// <summary>
/// One job of Naqel's own engine: resolves its reference, then moves its files one after another.
/// </summary>
internal sealed class NaqelJob : IEngineJob
{
    // Engine state keys: the job's files, the files done so far, and each unfinished file's checkpoint
    private const string FilesKey = "files";
    private const string DoneKey = "done";
    private const string CheckpointKey = "checkpoint:";

    private readonly NaqelEngine _engine;
    private readonly Lock _lock = new();
    private JobRequest _request;
    private CancellationTokenSource? _run;
    private Task? _running;
    private TransferOperation? _current;
    private volatile JobSnapshot _snapshot = JobSnapshot.Empty with { State = JobState.Paused };

    public NaqelJob(NaqelEngine engine, JobRequest request)
    {
        _engine = engine;
        _request = request;
    }

    public string Id => _request.JobId;

    public JobSnapshot Snapshot => _snapshot;

    public void Start()
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false })
            {
                return;
            }
            var run = new CancellationTokenSource();
            _run = run;
            _running = Task.Run(() => RunAsync(run.Token), CancellationToken.None);
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);
        Report(_snapshot with { State = JobState.Paused, Rate = 0 });
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    public async Task RemoveAsync(bool deleteData, CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);
        if (deleteData)
        {
            await DeleteDataAsync(cancellationToken).ConfigureAwait(false);
        }
        _engine.Forget(this);
    }

    // Each file goes to the target it went to before: an unfinished one's target discards what it kept through its own
    // abort, and a finished one is deleted where its storage can delete it
    private async Task DeleteDataAsync(CancellationToken token)
    {
        var done = await LoadDoneAsync(token).ConfigureAwait(false);
        foreach (var file in await LoadFilesAsync(token).ConfigureAwait(false))
        {
            var info = new SourceInfo { Name = file.Name, Length = file.Length };
            var target = await Resolve.TargetAsync(_request.Destination, file.Path, info, _engine.TargetResolvers, cancellationToken: token).ConfigureAwait(false);
            if (done.ContainsKey(file.Path))
            {
                // A write that kept both may have put this job's file under another name, so the path may name someone else's
                if (target is StorageTarget { Mode: not WriteMode.KeepBoth, Storage: IStorageDelete deleter } kept &&
                    await kept.Storage.GetAsync(kept.Path, token).ConfigureAwait(false) is { IsFolder: false } item)
                {
                    await deleter.DeleteAsync(item, token).ConfigureAwait(false);
                }
                continue;
            }

            var checkpoints = new EngineStateCheckpointStore(State, Id, CheckpointKey + file.Path);
            if (await checkpoints.LoadAsync(token).ConfigureAwait(false) is { ResumeToken: { } resumeToken } checkpoint)
            {
                try
                {
                    var session = await target.OpenAsync(new TargetOpenRequest(new SourceInfo { Name = file.Name, Length = checkpoint.Length }, resumeToken), token).ConfigureAwait(false);
                    await using (session.ConfigureAwait(false))
                    {
                        await session.AbortAsync(token).ConfigureAwait(false);
                    }
                }
                catch (TransferException ex) when (ex.Kind == TransferErrorKind.NotFound)
                {
                    // Nothing left to discard
                }
            }
        }
    }

    private async Task<List<KnownFile>> LoadFilesAsync(CancellationToken token)
    {
        var text = await State.GetAsync(Id, FilesKey, token).ConfigureAwait(false);
        try
        {
            return text == null ? [] : JsonSerializer.Deserialize<List<KnownFile>>(text) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public Task SetLimitsAsync(JobLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        TransferOperation? current;
        lock (_lock)
        {
            _request = _request with { Limits = limits };
            current = _current;
        }
        if (current != null)
        {
            Apply(current, limits);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the run, if there is one, and waits for it; the current transfer saves its checkpoint as it stops.
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? run;
        Task? running;
        lock (_lock)
        {
            run = _run;
            running = _running;
            _run = null;
            _running = null;
        }
        if (run == null || running == null)
        {
            return;
        }
        await run.CancelAsync().ConfigureAwait(false);
        await running.ConfigureAwait(false);
        run.Dispose();
    }

    private IEngineState State => _engine.Host.State;

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            Report(_snapshot with { State = JobState.Preparing, Error = null });
            var done = await LoadDoneAsync(token).ConfigureAwait(false);

            // The whole reference first, so the job knows all its files and their size before moving any
            var items = new List<ResolvedSource>();
            await foreach (var item in Resolve.SourcesAsync(_request.Reference, _engine.SourceResolvers, cancellationToken: token).ConfigureAwait(false))
            {
                // A single file needs its name before its target is known, such as to go into a folder of a storage; a
                // resolver that didn't describe it, to save a request, leaves that to this one request
                items.Add(item.Path.Length == 0 && item.Info?.Name is not { Length: > 0 }
                    ? new ResolvedSource(item.Source, item.Path, await item.Source.GetInfoAsync(token).ConfigureAwait(false))
                    : item);
            }
            var files = items.Select(item => done.TryGetValue(item.Path, out var finished)
                ? new JobFile(item.Path, NameOf(item), finished.Length, finished.Length ?? 0, Done: true, finished.Location)
                : new JobFile(item.Path, NameOf(item), item.Info?.Length, 0, Done: false)).ToList();
            // Kept so the job's data can be found again without resolving its reference, such as to delete it
            var known = files.Select(file => new KnownFile(file.Path, file.Name, file.Length)).ToList();
            await State.SetAsync(Id, FilesKey, JsonSerializer.Serialize(known), token).ConfigureAwait(false);
            Report(Summarize(files, JobState.Transferring, rate: 0));

            for (var i = 0; i < items.Count; i++)
            {
                if (files[i].Done)
                {
                    continue;
                }
                var item = items[i];
                var index = i;
                var target = await Resolve.TargetAsync(_request.Destination, item.Path, item.Info, _engine.TargetResolvers, cancellationToken: token).ConfigureAwait(false);
                var operation = _engine.Client.From(item.Source).To(target)
                    .WithCheckpointStore(new EngineStateCheckpointStore(State, Id, CheckpointKey + item.Path))
                    .Build();
                lock (_lock)
                {
                    _current = operation;
                    Apply(operation, _request.Limits);
                }

                TransferResult result;
                try
                {
                    result = await operation.RunAsync(new InlineProgress(progress =>
                    {
                        files[index] = files[index] with { Transferred = progress.Transferred, Length = progress.Total ?? files[index].Length };
                        Report(Summarize(files, JobState.Transferring, progress.CurrentRate));
                    }), token).ConfigureAwait(false);
                }
                finally
                {
                    lock (_lock)
                    {
                        _current = null;
                    }
                }

                files[index] = files[index] with { Transferred = result.Transferred, Length = result.Transferred, Done = true, Location = result.Location };
                done[item.Path] = new FinishedFile(result.Transferred, result.Location);
                await State.SetAsync(Id, DoneKey, JsonSerializer.Serialize(done), token).ConfigureAwait(false);
                Report(Summarize(files, JobState.Transferring, rate: 0));
            }

            Report(Summarize(files, JobState.Completed, rate: 0) with { ContentComplete = true });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Paused, removed or stopping; the transfer kept its checkpoint
        }
        catch (Exception ex)
        {
            Report(_snapshot with { State = JobState.Failed, Rate = 0, Error = JobError.From(ex) });
        }
    }

    private async Task<Dictionary<string, FinishedFile>> LoadDoneAsync(CancellationToken token)
    {
        var text = await State.GetAsync(Id, DoneKey, token).ConfigureAwait(false);
        if (text != null)
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, FinishedFile>>(text) ?? [];
            }
            catch (JsonException)
            {
                // A record that can't be read means moving the files again
            }
        }
        return [];
    }

    private void Report(JobSnapshot snapshot)
    {
        _snapshot = snapshot;
        _engine.Host.Report(Id, snapshot);
    }

    private void Apply(TransferOperation operation, JobLimits limits)
    {
        operation.SetMaxBytesPerSecond(limits.MaxBytesPerSecond);
        operation.SetMaxConnections(limits.MaxConnections ?? _engine.TransferOptions.MaxConnections);
    }

    private static JobSnapshot Summarize(List<JobFile> files, JobState state, double rate) => new()
    {
        State = state,
        Files = [.. files],
        Transferred = files.Sum(file => file.Transferred),
        Total = files.All(file => file.Length != null) ? files.Sum(file => file.Length!.Value) : null,
        Rate = rate
    };

    private string NameOf(ResolvedSource item)
    {
        if (item.Info?.Name is { Length: > 0 } name)
        {
            return name;
        }
        if (item.Path.Length > 0)
        {
            return item.Path[(item.Path.LastIndexOf('/') + 1)..];
        }
        var segments = _request.Reference.Segments;
        return segments.Length > 0 ? Uri.UnescapeDataString(segments[^1].TrimEnd('/')) : _request.Reference.ToString();
    }

    /// <summary>
    /// A file of the job that's done: its length and where it ended up.
    /// </summary>
    private sealed record FinishedFile(long? Length, string? Location);

    /// <summary>
    /// A file of the job, as its reference resolved: where it belongs, its name and its length.
    /// </summary>
    private sealed record KnownFile(string Path, string Name, long? Length);

    /// <summary>
    /// Hands progress on at once, on the transfer's own thread, unlike <see cref="Progress{T}"/>.
    /// </summary>
    private sealed class InlineProgress(Action<TransferProgress> handler) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => handler(value);
    }
}
