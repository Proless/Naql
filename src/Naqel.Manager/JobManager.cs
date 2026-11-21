using System.Collections.Concurrent;
using System.Threading.Channels;
using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// Runs jobs on engines: picks the engine for each new job, keeps a queue so only so many transfer at once, runs the
/// completion steps, tells observers what happens, and keeps everything in the job store so it survives a restart.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Call <see cref="StartAsync"/> once before anything else; it starts the engines and brings back the jobs
/// the store kept. Jobs that were transferring wait in the queue again; paused, failed and completed jobs stay so.</item>
/// <item>Commands such as adding or pausing a job run one at a time, in the order they're called.</item>
/// <item>Disposing the manager stops the engines, which keep what they need to continue the jobs on the next start.</item>
/// </list>
/// </remarks>
public sealed class JobManager : IAsyncDisposable
{
    private const string StepDataPrefix = "naqel.step.";

    private readonly JobManagerOptions _options;
    private readonly IJobStore _store;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<ManagedJob> _jobs = [];
    private readonly Channel<ManagerMessage> _reports = Channel.CreateUnbounded<ManagerMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Interactions _interactions;
    private readonly Accounts _accounts;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<ObserverQueue> _observers = [];
    private readonly ConcurrentDictionary<Task, bool> _steps = new();
    private volatile JobInfo[] _published = [];
    private Task? _reportLoop;
    private int _maxActiveJobs;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// A manager for the engines, observers, completion steps and store in <paramref name="options"/>.
    /// </summary>
    public JobManager(JobManagerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.Engines.Count == 0)
        {
            throw new ArgumentException("A manager needs at least one engine", nameof(options));
        }
        if (options.Engines.Select(engine => engine.Name).Distinct(StringComparer.Ordinal).Count() != options.Engines.Count)
        {
            throw new ArgumentException("Every engine needs a name of its own", nameof(options));
        }
        _store = options.Store;
        _maxActiveJobs = options.MaxActiveJobs;
        _interactions = new Interactions(options.TimeProvider, jobId => _reports.Writer.TryWrite(new AttentionChange(jobId)));
        _accounts = new Accounts(options.Secrets, _interactions);
        foreach (var provider in options.AccountProviders)
        {
            _accounts.AddProvider(provider);
        }
    }

    /// <summary>
    /// Raised when an observer threw while handling an event. The observer still gets the next events.
    /// </summary>
    public event Action<IJobObserver, JobEvent, Exception>? ObserverFailed;

    /// <summary>
    /// Raised when the manager couldn't take in something an engine or a question reported, such as when the job store
    /// failed to save; the manager goes on with what comes next.
    /// </summary>
    public event Action<Exception>? Faulted;

    /// <summary>
    /// Every job, in the order they were added, as they are now. Safe to read from any thread.
    /// </summary>
    public IReadOnlyList<JobInfo> Jobs => _published;

    /// <summary>
    /// How many jobs may transfer at once.
    /// </summary>
    public int MaxActiveJobs => Volatile.Read(ref _maxActiveJobs);

    /// <summary>
    /// The storage locations jobs save to and read from; they can be added and removed at any time.
    /// </summary>
    public StorageLocations Locations => _options.Locations;

    /// <summary>
    /// The questions waiting for the user's answer.
    /// </summary>
    public Interactions Interactions => _interactions;

    /// <summary>
    /// The user's accounts.
    /// </summary>
    public Accounts Accounts => _accounts;

    /// <summary>
    /// The job with <paramref name="jobId"/>, as it is now, or <c>null</c>.
    /// </summary>
    public JobInfo? Find(string jobId) => Array.Find(_published, job => job.Id == jobId);

    /// <summary>
    /// Starts the engines, brings back the jobs the store kept, and starts the queue.
    /// </summary>
    /// <exception cref="InvalidOperationException">The manager already started</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The manager already started");
            }

            foreach (var observer in _options.Observers)
            {
                _observers.Add(new ObserverQueue(observer, OnObserverFailed, _shutdown.Token));
            }
            _reportLoop = Task.Run(ProcessReportsAsync, CancellationToken.None);

            await _accounts.LoadAsync(cancellationToken).ConfigureAwait(false);
            var host = new EngineHost(_reports.Writer, _store, _interactions);
            foreach (var engine in _options.Engines)
            {
                await engine.StartAsync(host, cancellationToken).ConfigureAwait(false);
            }

            foreach (var stored in await _store.LoadAsync(cancellationToken).ConfigureAwait(false))
            {
                await RestoreAsync(stored, cancellationToken).ConfigureAwait(false);
            }
            _started = true;
            Publish();
            await ScheduleAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Adds a job, which waits in the queue until it may start. The first engine that handles
    /// <paramref name="reference"/> runs it.
    /// </summary>
    /// <param name="reference">What to transfer, such as a link or a folder</param>
    /// <param name="destination">Where to put it, such as a folder</param>
    /// <param name="labels">Labels for the job, if any</param>
    /// <param name="limits">The job's own limits, if any</param>
    /// <param name="cancellationToken">Stops adding</param>
    /// <returns>The job, as it was added</returns>
    /// <exception cref="TransferException">No engine handles <paramref name="reference"/> (<see cref="TransferErrorKind.Unsupported"/>)</exception>
    public async Task<JobInfo> AddAsync(Uri reference, Uri destination, IEnumerable<string>? labels = null, JobLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(destination);
        var engine = _options.Engines.FirstOrDefault(engine => engine.CanHandle(reference))
            ?? throw new TransferException($"No engine handles {reference}", TransferErrorKind.Unsupported, side: TransferSide.Source);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotRunning();
            var info = new JobInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Reference = reference,
                Destination = destination,
                Engine = engine.Name,
                Limits = limits ?? JobLimits.None,
                Labels = new HashSet<string>(labels ?? [], StringComparer.Ordinal),
                Added = Now
            };
            var job = new ManagedJob(info, engine, engineJobId: null);
            _jobs.Add(job);
            await SaveAsync(job).ConfigureAwait(false);
            Emit(JobEventKind.Added, job, previous: null);
            await ScheduleAsync().ConfigureAwait(false);
            return job.Info;
        }
        finally
        {
            Publish();
            _lock.Release();
        }
    }

    /// <summary>
    /// Pauses a job, so it continues where it stopped when resumed. A queued job stays out of the queue until then.
    /// </summary>
    /// <exception cref="KeyNotFoundException">There's no such job</exception>
    /// <exception cref="InvalidOperationException">The job is finishing, completed or failed</exception>
    /// <exception cref="NotSupportedException">The job's engine can't pause</exception>
    public Task PauseAsync(string jobId, CancellationToken cancellationToken = default) => WithJobAsync(jobId, async job =>
    {
        switch (job.Info.State)
        {
            case JobState.Paused:
                return;
            case JobState.Finishing or JobState.Completed or JobState.Failed:
                throw new InvalidOperationException($"A job that is {job.Info.State} can't be paused");
            case JobState.Queued:
                break;
            default:
                if (job.EngineJob != null)
                {
                    if (!job.Engine!.Capabilities.Pause)
                    {
                        throw new NotSupportedException($"The engine {job.Engine.Name} can't pause jobs");
                    }
                    await job.EngineJob.PauseAsync(_shutdown.Token).ConfigureAwait(false);
                }
                break;
        }
        await ChangeAsync(job, job.Info with { State = JobState.Paused }, JobEventKind.StateChanged).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>
    /// Continues a paused job, or tries a failed one again; it waits in the queue for its turn. A job that failed in a
    /// completion step goes on with that step.
    /// </summary>
    /// <exception cref="KeyNotFoundException">There's no such job</exception>
    /// <exception cref="InvalidOperationException">The job completed</exception>
    public Task ResumeAsync(string jobId, CancellationToken cancellationToken = default) => WithJobAsync(jobId, async job =>
    {
        switch (job.Info.State)
        {
            case JobState.Completed or JobState.Seeding:
                throw new InvalidOperationException("A completed job can't be resumed");
            case JobState.Failed when job.Engine == null:
                throw new InvalidOperationException($"The engine {job.Info.Engine} isn't available");
            case JobState.Failed when job.Info.Progress.ContentComplete:
                await BeginFinishingAsync(job).ConfigureAwait(false);
                break;
            case JobState.Paused or JobState.Failed:
                await ChangeAsync(job, job.Info with { State = JobState.Queued, Error = null }, JobEventKind.StateChanged).ConfigureAwait(false);
                break;
        }
    }, cancellationToken);

    /// <summary>
    /// Stops a job for good and forgets it. With <paramref name="deleteData"/>, its engine also deletes what it
    /// transferred.
    /// </summary>
    /// <exception cref="KeyNotFoundException">There's no such job</exception>
    /// <exception cref="NotSupportedException">The job's engine can't delete what it transferred</exception>
    public Task RemoveAsync(string jobId, bool deleteData = false, CancellationToken cancellationToken = default) => WithJobAsync(jobId, async job =>
    {
        if (deleteData && job.Engine is { Capabilities.DeleteData: false } engine)
        {
            throw new NotSupportedException($"The engine {engine.Name} can't delete what its jobs transferred");
        }
        if (job.Finishing is { } finishing)
        {
            await finishing.CancelAsync().ConfigureAwait(false);
        }
        _interactions.CancelFor(job.Info.Id);
        if (job.EngineJob != null)
        {
            await job.EngineJob.RemoveAsync(deleteData, _shutdown.Token).ConfigureAwait(false);
        }
        _jobs.Remove(job);
        await _store.DeleteAsync(job.Info.Id, _shutdown.Token).ConfigureAwait(false);
        Emit(JobEventKind.Removed, job, previous: null);
    }, cancellationToken);

    /// <summary>
    /// Changes a job's own limits, also while it runs.
    /// </summary>
    /// <exception cref="KeyNotFoundException">There's no such job</exception>
    /// <exception cref="NotSupportedException">The job's engine can't limit single jobs</exception>
    public Task SetJobLimitsAsync(string jobId, JobLimits limits, CancellationToken cancellationToken = default) => WithJobAsync(jobId, async job =>
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (job.Engine is { Capabilities.JobLimits: false } engine)
        {
            throw new NotSupportedException($"The engine {engine.Name} can't limit single jobs");
        }
        if (job.EngineJob != null)
        {
            await job.EngineJob.SetLimitsAsync(limits, _shutdown.Token).ConfigureAwait(false);
        }
        job.Info = job.Info with { Limits = limits };
        await SaveAsync(job).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>
    /// Changes the limits of every engine that can limit its jobs together. Each engine gets the whole limit.
    /// </summary>
    public async Task SetEngineLimitsAsync(EngineLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotRunning();
            foreach (var engine in _options.Engines.Where(engine => engine.Capabilities.EngineLimits))
            {
                await engine.SetLimitsAsync(limits, _shutdown.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Changes how many jobs may transfer at once. More start queued jobs at once; fewer let running jobs finish.
    /// </summary>
    public async Task SetMaxActiveJobsAsync(int maxActiveJobs, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxActiveJobs);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotRunning();
            Volatile.Write(ref _maxActiveJobs, maxActiveJobs);
            await ScheduleAsync().ConfigureAwait(false);
        }
        finally
        {
            Publish();
            _lock.Release();
        }
    }

    /// <summary>
    /// Replaces a job's labels.
    /// </summary>
    /// <exception cref="KeyNotFoundException">There's no such job</exception>
    public Task SetLabelsAsync(string jobId, IEnumerable<string> labels, CancellationToken cancellationToken = default) => WithJobAsync(jobId, async job =>
    {
        ArgumentNullException.ThrowIfNull(labels);
        job.Info = job.Info with { Labels = new HashSet<string>(labels, StringComparer.Ordinal) };
        await SaveAsync(job).ConfigureAwait(false);
        Emit(JobEventKind.DataChanged, job, previous: null);
    }, cancellationToken);

    /// <summary>
    /// Keeps <paramref name="value"/> under <paramref name="key"/> in a job's data, or deletes it when it's <c>null</c>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">There's no such job</exception>
    public Task SetDataAsync(string jobId, string key, string? value, CancellationToken cancellationToken = default) => WithJobAsync(jobId, async job =>
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        job.Info = job.Info with { Data = WithData(job.Info.Data, [new(key, value)]) };
        await SaveAsync(job).ConfigureAwait(false);
        Emit(JobEventKind.DataChanged, job, previous: null);
    }, cancellationToken);

    /// <summary>
    /// Stops the queue, the observers and the engines. The engines keep what they need to continue their jobs, and
    /// the store keeps the jobs as they were, so the next start goes on with them.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        finally
        {
            _lock.Release();
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _reports.Writer.TryComplete();
        if (_reportLoop != null)
        {
            await _reportLoop.ConfigureAwait(false);
        }
        await Task.WhenAll(_steps.Keys).ConfigureAwait(false);
        foreach (var observer in _observers)
        {
            await observer.StopAsync().ConfigureAwait(false);
        }
        foreach (var engine in _options.Engines)
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }
        _shutdown.Dispose();
    }

    private DateTimeOffset Now => _options.TimeProvider.GetUtcNow();

    private void ThrowIfNotRunning()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
        {
            throw new InvalidOperationException("Start the manager first");
        }
    }

    private async Task WithJobAsync(string jobId, Func<ManagedJob, Task> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotRunning();
            var job = _jobs.Find(job => job.Info.Id == jobId) ?? throw new KeyNotFoundException($"There's no job {jobId}");
            await action(job).ConfigureAwait(false);
            await ScheduleAsync().ConfigureAwait(false);
        }
        finally
        {
            Publish();
            _lock.Release();
        }
    }

    // A job that was running when the app stopped waits in the queue again; its engine gets it back paused
    private async Task RestoreAsync(StoredJob stored, CancellationToken cancellationToken)
    {
        var engine = _options.Engines.FirstOrDefault(engine => engine.Name == stored.Job.Engine);
        var job = new ManagedJob(stored.Job, engine, stored.EngineJobId);
        _jobs.Add(job);

        if (engine == null)
        {
            if (job.Info.State is not (JobState.Completed or JobState.Seeding))
            {
                job.Info = job.Info with
                {
                    State = JobState.Failed,
                    Error = new JobError(TransferErrorKind.Unsupported, $"The engine {stored.Job.Engine} isn't available", IsTransient: false)
                };
                await SaveAsync(job).ConfigureAwait(false);
            }
            return;
        }

        if (job.Info.State != JobState.Completed && job.EngineJobId is { } engineJobId)
        {
            try
            {
                job.EngineJob = await engine.AttachAsync(job.Request, engineJobId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // One job the engine can't take back doesn't keep the others from starting
                job.Info = job.Info with { State = JobState.Failed, Error = JobError.From(ex) };
                await SaveAsync(job).ConfigureAwait(false);
                return;
            }
            if (job.EngineJob == null)
            {
                // The engine forgot the job; it starts over
                job.EngineJobId = null;
            }
        }

        var state = job.Info.State switch
        {
            JobState.Paused or JobState.Failed or JobState.Completed or JobState.Finishing => job.Info.State,
            _ => JobState.Queued
        };
        if (state != job.Info.State)
        {
            job.Info = job.Info with { State = state };
        }
        await SaveAsync(job).ConfigureAwait(false);
        if (state == JobState.Finishing)
        {
            StartSteps(job);
        }
    }

    // Starts queued jobs, in the order they were added, while fewer than allowed transfer
    private async Task ScheduleAsync()
    {
        var active = _jobs.Count(job => IsActive(job.Info.State));
        foreach (var job in _jobs.Where(job => job.Info.State == JobState.Queued).ToList())
        {
            if (active >= MaxActiveJobs || _shutdown.IsCancellationRequested)
            {
                return;
            }
            await StartJobAsync(job).ConfigureAwait(false);
            if (IsActive(job.Info.State))
            {
                active++;
            }
        }
    }

    private async Task StartJobAsync(ManagedJob job)
    {
        try
        {
            job.StartedAfter = ManagerMessage.Last;
            if (job.EngineJob == null)
            {
                job.EngineJob = await job.Engine!.AddAsync(job.Request, _shutdown.Token).ConfigureAwait(false);
                job.EngineJobId = job.EngineJob.Id;
            }
            else
            {
                await job.EngineJob.ResumeAsync(_shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!_shutdown.IsCancellationRequested)
        {
            await FailAsync(job, JobError.From(ex)).ConfigureAwait(false);
            return;
        }

        // The engine's reports take it from here; until the first arrives, the job is preparing
        job.Info = job.Info with { Progress = job.EngineJob.Snapshot };
        await ChangeAsync(job, job.Info with { State = JobState.Preparing }, JobEventKind.Started).ConfigureAwait(false);
    }

    private async Task ProcessReportsAsync()
    {
        try
        {
            await foreach (var message in _reports.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                await _lock.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                try
                {
                    var job = _jobs.Find(job => job.Info.Id == message.JobId);
                    if (job == null)
                    {
                        continue;
                    }
                    // A report from before the job last started, such as that it paused, says nothing about its new run
                    if (message is EngineReport report && job.EngineJob != null && report.Sequence > job.StartedAfter)
                    {
                        await ApplyAsync(job, report.Snapshot).ConfigureAwait(false);
                    }
                    else if (message is AttentionChange)
                    {
                        await UpdateAttentionAsync(job).ConfigureAwait(false);
                    }
                    await ScheduleAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !_shutdown.IsCancellationRequested)
                {
                    // One message that couldn't be taken in mustn't stop the ones after it
                    OnFaulted(ex);
                }
                finally
                {
                    Publish();
                    _lock.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Shutting down
        }
    }

    // What an engine reports drives a running job's state; for a job the manager holds, such as a paused one, only
    // its progress counts
    private async Task ApplyAsync(ManagedJob job, JobSnapshot snapshot)
    {
        job.EngineState = snapshot.State;
        var info = job.Info with { Progress = snapshot };
        switch (job.Info.State)
        {
            case JobState.Queued or JobState.Paused or JobState.Failed or JobState.Finishing:
                job.Info = info;
                return;

            case JobState.Completed or JobState.Seeding:
                // After completing, only seeding starting or stopping changes the state
                var after = snapshot.State == JobState.Seeding ? JobState.Seeding : JobState.Completed;
                if (after != job.Info.State)
                {
                    await ChangeAsync(job, info with { State = after }, JobEventKind.StateChanged).ConfigureAwait(false);
                }
                else
                {
                    job.Info = info;
                }
                return;
        }

        if (snapshot.ContentComplete)
        {
            job.Info = info;
            await BeginFinishingAsync(job).ConfigureAwait(false);
        }
        else if (snapshot.State == JobState.Failed)
        {
            job.Info = info;
            await FailAsync(job, snapshot.Error ?? new JobError(TransferErrorKind.Unknown, "The engine reported a failure without an error", IsTransient: false)).ConfigureAwait(false);
        }
        else if (job.Attention > 0)
        {
            // The job waits for the user; it needs attention whatever the engine is doing meanwhile
            job.Info = info;
        }
        else if (snapshot.State is JobState.Completed or JobState.Seeding or JobState.Queued or JobState.Finishing)
        {
            // Not a state an engine reports without its content being complete; only the progress counts
            job.Info = info;
        }
        else if (snapshot.State != job.Info.State)
        {
            await ChangeAsync(job, info with { State = snapshot.State }, JobEventKind.StateChanged).ConfigureAwait(false);
        }
        else
        {
            job.Info = info;
        }
    }

    // A running job with a question waiting needs attention, and goes back to what its engine does once none waits
    private async Task UpdateAttentionAsync(ManagedJob job)
    {
        job.Attention = _interactions.CountFor(job.Info.Id);
        if (job.Attention > 0 && IsActive(job.Info.State) && job.Info.State != JobState.NeedsAttention)
        {
            await ChangeAsync(job, job.Info with { State = JobState.NeedsAttention }, JobEventKind.StateChanged).ConfigureAwait(false);
        }
        else if (job.Attention == 0 && job.Info.State == JobState.NeedsAttention && job.EngineState != JobState.NeedsAttention)
        {
            var state = IsActive(job.EngineState) ? job.EngineState : JobState.Preparing;
            await ChangeAsync(job, job.Info with { State = state }, JobEventKind.StateChanged).ConfigureAwait(false);
        }
    }

    private async Task BeginFinishingAsync(ManagedJob job)
    {
        await ChangeAsync(job, job.Info with { State = JobState.Finishing, Error = null }, JobEventKind.StateChanged).ConfigureAwait(false);
        StartSteps(job);
    }

    private void StartSteps(ManagedJob job)
    {
        var finishing = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        job.Finishing = finishing;
        var steps = Task.Run(() => RunStepsAsync(job, finishing), CancellationToken.None);
        _steps[steps] = true;
        _ = steps.ContinueWith(done => _steps.TryRemove(done, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // The steps run outside the lock, so other jobs go on meanwhile; what they did is kept under it
    private async Task RunStepsAsync(ManagedJob job, CancellationTokenSource finishing)
    {
        try
        {
            foreach (var step in _options.CompletionSteps)
            {
                JobInfo before;
                await _lock.WaitAsync(finishing.Token).ConfigureAwait(false);
                try
                {
                    before = job.Info;
                }
                finally
                {
                    _lock.Release();
                }
                if (before.Data.ContainsKey(StepDataPrefix + step.Name))
                {
                    continue;
                }

                var context = new CompletionContext(before);
                Exception? failure = null;
                try
                {
                    await step.RunAsync(context, finishing.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (finishing.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                await _lock.WaitAsync(finishing.Token).ConfigureAwait(false);
                try
                {
                    var changes = context.TakeChanges().ToList();
                    if (failure == null)
                    {
                        changes.Add(new(StepDataPrefix + step.Name, "done"));
                    }
                    job.Info = job.Info with { Data = WithData(job.Info.Data, changes) };
                    if (failure != null)
                    {
                        await FailAsync(job, JobError.From(failure)).ConfigureAwait(false);
                        return;
                    }
                    await SaveAsync(job).ConfigureAwait(false);
                }
                finally
                {
                    Publish();
                    _lock.Release();
                }
            }

            await _lock.WaitAsync(finishing.Token).ConfigureAwait(false);
            try
            {
                var state = job.EngineState == JobState.Seeding ? JobState.Seeding : JobState.Completed;
                // A job that completed before, such as a seeding one after a restart, doesn't complete again
                var kind = job.Info.Completed == null ? JobEventKind.Completed : JobEventKind.StateChanged;
                await ChangeAsync(job, job.Info with { State = state, Completed = job.Info.Completed ?? Now }, kind).ConfigureAwait(false);
                await ScheduleAsync().ConfigureAwait(false);
            }
            finally
            {
                Publish();
                _lock.Release();
            }
        }
        catch (OperationCanceledException) when (finishing.IsCancellationRequested)
        {
            // Removed, or shutting down: a step that didn't finish runs again on the next start
        }
        finally
        {
            if (job.Finishing == finishing)
            {
                job.Finishing = null;
            }
            finishing.Dispose();
        }
    }

    private Task FailAsync(ManagedJob job, JobError error) =>
        ChangeAsync(job, job.Info with { State = JobState.Failed, Error = error }, JobEventKind.Failed);

    private async Task ChangeAsync(ManagedJob job, JobInfo changed, JobEventKind kind)
    {
        var previous = job.Info.State;
        job.Info = changed;
        await SaveAsync(job).ConfigureAwait(false);
        Emit(kind, job, previous);
    }

    private Task SaveAsync(ManagedJob job) => _store.SaveAsync(job.Stored, _shutdown.Token);

    private void Emit(JobEventKind kind, ManagedJob job, JobState? previous)
    {
        var jobEvent = new JobEvent(kind, job.Info, Now) { PreviousState = previous };
        foreach (var observer in _observers)
        {
            observer.Post(jobEvent);
        }
    }

    private void OnObserverFailed(IJobObserver observer, JobEvent jobEvent, Exception exception)
    {
        try
        {
            ObserverFailed?.Invoke(observer, jobEvent, exception);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A handler's own failure mustn't stop the observer's events
        }
    }

    private void OnFaulted(Exception exception)
    {
        try
        {
            Faulted?.Invoke(exception);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A handler's own failure mustn't stop the manager
        }
    }

    private void Publish() => _published = [.. _jobs.Select(job => job.Info)];

    private static bool IsActive(JobState state) =>
        state is JobState.Preparing or JobState.Checking or JobState.Transferring or JobState.NeedsAttention;

    private static Dictionary<string, string> WithData(IReadOnlyDictionary<string, string> data, IEnumerable<KeyValuePair<string, string?>> changes)
    {
        var updated = new Dictionary<string, string>(data, StringComparer.Ordinal);
        foreach (var (key, value) in changes)
        {
            if (value == null)
            {
                updated.Remove(key);
            }
            else
            {
                updated[key] = value;
            }
        }
        return updated;
    }
}
