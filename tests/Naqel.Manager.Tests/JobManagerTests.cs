using Naqel.Manager.Tests.Fakes;
using Naqel.Plugins;

namespace Naqel.Manager.Tests;

/// <summary>
/// The manager with fake engines: routing, the queue, states, completion steps, hooks, data and restarts.
/// </summary>
public sealed class JobManagerTests : IAsyncDisposable
{
    private static readonly Uri Destination = new("file:///downloads/");
    private readonly List<JobManager> _managers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            await manager.DisposeAsync();
        }
    }

    private async Task<JobManager> StartAsync(JobManagerOptions options)
    {
        var manager = new JobManager(options);
        _managers.Add(manager);
        await manager.StartAsync(Token);
        return manager;
    }

    private static JobManagerOptions Options(params ITransferEngine[] engines)
    {
        var options = new JobManagerOptions();
        foreach (var engine in engines)
        {
            options.Engines.Add(engine);
        }
        return options;
    }

    private static async Task EventuallyAsync(Func<bool> condition, string because)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            try
            {
                await Task.Delay(5, timeout.Token);
            }
            catch (OperationCanceledException) when (!Token.IsCancellationRequested)
            {
                Assert.Fail($"Timed out waiting until {because}");
            }
        }
    }

    private static Task StateAsync(JobManager manager, string jobId, JobState state) =>
        EventuallyAsync(() => manager.Find(jobId)?.State == state, $"the job is {state}, not {manager.Find(jobId)?.State}");

    private static Uri Link(int n) => new($"fake://host/{n}");

    #region Routing and the queue

    [Fact]
    public async Task A_job_goes_to_the_first_engine_that_handles_its_reference()
    {
        var torrents = new FakeEngine("torrents", "magnet");
        var fallback = new FakeEngine("naqel", "fake");
        var manager = await StartAsync(Options(torrents, fallback));

        var magnet = await manager.AddAsync(new Uri("magnet:?xt=urn:btih:abc"), Destination, cancellationToken: Token);
        var link = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);

        Assert.Equal("torrents", magnet.Engine);
        Assert.Equal("naqel", link.Engine);
        await StateAsync(manager, magnet.Id, JobState.Preparing);
        Assert.Single(torrents.Jobs);
        Assert.Single(fallback.Jobs);
    }

    [Fact]
    public async Task A_reference_no_engine_handles_is_refused()
    {
        var manager = await StartAsync(Options(new FakeEngine()));

        var ex = await Assert.ThrowsAsync<TransferException>(() => manager.AddAsync(new Uri("ftp://host/a"), Destination, cancellationToken: Token));

        Assert.Equal(TransferErrorKind.Unsupported, ex.Kind);
        Assert.Empty(manager.Jobs);
    }

    [Fact]
    public async Task Jobs_beyond_the_limit_wait_in_the_queue_until_a_running_one_completes()
    {
        var engine = new FakeEngine();
        var options = Options(engine);
        options.MaxActiveJobs = 2;
        var manager = await StartAsync(options);

        var jobs = new List<JobInfo>();
        for (var i = 0; i < 3; i++)
        {
            jobs.Add(await manager.AddAsync(Link(i), Destination, cancellationToken: Token));
        }

        Assert.Equal(JobState.Queued, manager.Find(jobs[2].Id)!.State);
        Assert.Equal(2, engine.Jobs.Count);

        engine.Job(jobs[0].Id).Complete();
        await StateAsync(manager, jobs[0].Id, JobState.Completed);
        await StateAsync(manager, jobs[2].Id, JobState.Preparing);
        Assert.Equal(3, engine.Jobs.Count);
    }

    [Fact]
    public async Task Raising_the_limit_starts_queued_jobs_at_once()
    {
        var engine = new FakeEngine();
        var options = Options(engine);
        options.MaxActiveJobs = 1;
        var manager = await StartAsync(options);
        var first = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);
        var second = await manager.AddAsync(Link(2), Destination, cancellationToken: Token);

        await manager.SetMaxActiveJobsAsync(2, Token);

        Assert.Equal(JobState.Preparing, manager.Find(second.Id)!.State);
        Assert.Equal(2, manager.MaxActiveJobs);
        Assert.Equal(JobState.Preparing, manager.Find(first.Id)!.State);
    }

    [Fact]
    public async Task Pausing_a_job_frees_its_place_and_resuming_puts_it_back_in_the_queue()
    {
        var engine = new FakeEngine();
        var options = Options(engine);
        options.MaxActiveJobs = 1;
        var manager = await StartAsync(options);
        var first = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);
        var second = await manager.AddAsync(Link(2), Destination, cancellationToken: Token);

        await manager.PauseAsync(first.Id, Token);

        Assert.Equal(JobState.Paused, manager.Find(first.Id)!.State);
        Assert.Equal(1, engine.Job(first.Id).Pauses);
        Assert.Equal(JobState.Preparing, manager.Find(second.Id)!.State);

        await manager.ResumeAsync(first.Id, Token);
        Assert.Equal(JobState.Queued, manager.Find(first.Id)!.State);

        engine.Job(second.Id).Complete();
        await StateAsync(manager, first.Id, JobState.Transferring);
        Assert.Equal(1, engine.Job(first.Id).Resumes);
    }

    [Fact]
    public async Task A_queued_job_can_be_paused_and_never_starts_until_resumed()
    {
        var engine = new FakeEngine();
        var options = Options(engine);
        options.MaxActiveJobs = 1;
        var manager = await StartAsync(options);
        var first = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);
        var second = await manager.AddAsync(Link(2), Destination, cancellationToken: Token);

        await manager.PauseAsync(second.Id, Token);
        engine.Job(first.Id).Complete();
        await StateAsync(manager, first.Id, JobState.Completed);

        Assert.Equal(JobState.Paused, manager.Find(second.Id)!.State);
        Assert.Single(engine.Jobs);
    }

    #endregion

    #region States from the engine

    [Fact]
    public async Task What_the_engine_reports_becomes_the_jobs_state_and_progress()
    {
        var engine = new FakeEngine();
        var manager = await StartAsync(Options(engine));
        var job = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);

        engine.Job(job.Id).Transfer(400);

        await StateAsync(manager, job.Id, JobState.Transferring);
        await EventuallyAsync(() => manager.Find(job.Id)!.Progress.Transferred == 400, "the progress arrives");
        Assert.Equal(1_000, manager.Find(job.Id)!.Progress.Total);
    }

    [Fact]
    public async Task A_job_the_engine_fails_is_failed_with_the_engines_error_and_can_be_tried_again()
    {
        var engine = new FakeEngine();
        var manager = await StartAsync(Options(engine));
        var job = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);

        engine.Job(job.Id).Fail(TransferErrorKind.Timeout);
        await StateAsync(manager, job.Id, JobState.Failed);
        Assert.Equal(TransferErrorKind.Timeout, manager.Find(job.Id)!.Error!.Kind);

        await manager.ResumeAsync(job.Id, Token);
        await StateAsync(manager, job.Id, JobState.Transferring);
        Assert.Null(manager.Find(job.Id)!.Error);
        Assert.Equal(1, engine.Job(job.Id).Resumes);
    }

    [Fact]
    public async Task An_engine_that_refuses_a_job_fails_it_and_the_queue_goes_on()
    {
        var engine = new FakeEngine { AddFailure = new TransferException("Not this one", TransferErrorKind.AccessDenied) };
        var options = Options(engine);
        options.MaxActiveJobs = 1;
        var manager = await StartAsync(options);

        var refused = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);
        Assert.Equal(JobState.Failed, manager.Find(refused.Id)!.State);
        Assert.Equal(TransferErrorKind.AccessDenied, manager.Find(refused.Id)!.Error!.Kind);

        engine.AddFailure = null;
        var next = await manager.AddAsync(Link(2), Destination, cancellationToken: Token);
        Assert.Equal(JobState.Preparing, manager.Find(next.Id)!.State);
    }

    [Fact]
    public async Task A_seeding_job_completes_once_and_then_follows_the_engine_between_seeding_and_completed()
    {
        var engine = new FakeEngine();
        var observer = new RecordingObserver();
        var options = Options(engine);
        options.Observers.Add(observer);
        var manager = await StartAsync(options);
        var job = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);

        engine.Job(job.Id).Complete(seeding: true);
        await StateAsync(manager, job.Id, JobState.Seeding);
        Assert.NotNull(manager.Find(job.Id)!.Completed);

        engine.Job(job.Id).Report(engine.Job(job.Id).Snapshot with { State = JobState.Completed });
        await StateAsync(manager, job.Id, JobState.Completed);
        await EventuallyAsync(() => observer.Kinds(job.Id).Count == 5, "the observer has every change");
        Assert.Equal(
            [JobEventKind.Added, JobEventKind.Started, JobEventKind.StateChanged, JobEventKind.Completed, JobEventKind.StateChanged],
            observer.Kinds(job.Id));
        Assert.Equal([JobState.Finishing, JobState.Seeding, JobState.Completed], observer.Events.Skip(2).Select(e => e.Job.State));
    }

    #endregion

    #region Completion steps

    [Fact]
    public async Task Completion_steps_run_in_order_before_the_job_completes_and_keep_their_data()
    {
        var engine = new FakeEngine();
        var log = new List<string>();
        var first = new RecordingStep("first", log) { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var second = new RecordingStep("second", log);
        var options = Options(engine);
        options.CompletionSteps.Add(first);
        options.CompletionSteps.Add(second);
        var manager = await StartAsync(options);
        var job = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);

        engine.Job(job.Id).Complete();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(JobState.Finishing, manager.Find(job.Id)!.State);
        Assert.Null(manager.Find(job.Id)!.Completed);

        first.Gate.SetResult();
        await StateAsync(manager, job.Id, JobState.Completed);

        Assert.Equal(["first", "second"], log);
        var done = manager.Find(job.Id)!;
        Assert.Equal("ran", done.Data["test.first"]);
        Assert.Equal("ran", done.Data["test.second"]);
        // The steps see where the files ended up
        Assert.Equal("/downloads/file.bin", Assert.Single(first.Saw!.Progress.Files).Location);
    }

    [Fact]
    public async Task A_job_whose_steps_run_doesnt_hold_a_place_in_the_queue()
    {
        var engine = new FakeEngine();
        var step = new RecordingStep("slow") { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var options = Options(engine);
        options.MaxActiveJobs = 1;
        options.CompletionSteps.Add(step);
        var manager = await StartAsync(options);
        var first = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);
        var second = await manager.AddAsync(Link(2), Destination, cancellationToken: Token);

        engine.Job(first.Id).Complete();
        await step.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        await StateAsync(manager, second.Id, JobState.Preparing);
        step.Gate.SetResult();
        await StateAsync(manager, first.Id, JobState.Completed);
    }

    [Fact]
    public async Task A_failing_step_fails_the_job_and_resuming_goes_on_from_that_step()
    {
        var engine = new FakeEngine();
        var first = new RecordingStep("first");
        var second = new RecordingStep("second") { Failure = new TransferException("The share is full", TransferErrorKind.InsufficientStorage) };
        var options = Options(engine);
        options.CompletionSteps.Add(first);
        options.CompletionSteps.Add(second);
        var manager = await StartAsync(options);
        var job = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);

        engine.Job(job.Id).Complete();
        await StateAsync(manager, job.Id, JobState.Failed);
        Assert.Equal(TransferErrorKind.InsufficientStorage, manager.Find(job.Id)!.Error!.Kind);
        // What the failed step set is kept too
        Assert.Equal("ran", manager.Find(job.Id)!.Data["test.second"]);

        second.Failure = null;
        await manager.ResumeAsync(job.Id, Token);
        await StateAsync(manager, job.Id, JobState.Completed);

        Assert.Equal(1, first.Runs);
        Assert.Equal(2, second.Runs);
        // The engine's part was done; only the steps ran again
        Assert.Equal(0, engine.Job(job.Id).Resumes);
    }

    #endregion

    #region Observers

    [Fact]
    public async Task Observers_get_every_event_in_order()
    {
        var engine = new FakeEngine();
        var observer = new RecordingObserver();
        var options = Options(engine);
        options.Observers.Add(observer);
        var manager = await StartAsync(options);
        var job = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);

        engine.Job(job.Id).Transfer(500);
        await StateAsync(manager, job.Id, JobState.Transferring);
        engine.Job(job.Id).Complete();
        await StateAsync(manager, job.Id, JobState.Completed);
        await manager.RemoveAsync(job.Id, cancellationToken: Token);

        await EventuallyAsync(() => observer.Kinds(job.Id).Contains(JobEventKind.Removed), "the observer heard about the removal");
        Assert.Equal(
            [JobEventKind.Added, JobEventKind.Started, JobEventKind.StateChanged, JobEventKind.StateChanged, JobEventKind.Completed, JobEventKind.Removed],
            observer.Kinds(job.Id));
        var states = observer.Events.Where(e => e.Kind == JobEventKind.StateChanged).Select(e => (e.PreviousState, e.Job.State));
        Assert.Equal([(JobState.Preparing, JobState.Transferring), (JobState.Transferring, JobState.Finishing)], states);
    }

    [Fact]
    public async Task A_failing_or_slow_observer_never_holds_up_a_job()
    {
        var engine = new FakeEngine();
        var gate = new TaskCompletionSource();
        var slow = new RecordingObserver { Gate = gate.Task };
        var failing = new RecordingObserver { Throws = true };
        var options = Options(engine);
        options.Observers.Add(slow);
        options.Observers.Add(failing);
        var manager = await StartAsync(options);
        var failures = 0;
        manager.ObserverFailed += (_, _, _) => Interlocked.Increment(ref failures);

        var job = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);
        engine.Job(job.Id).Complete();

        await StateAsync(manager, job.Id, JobState.Completed);
        Assert.Empty(slow.Events);
        await EventuallyAsync(() => Volatile.Read(ref failures) >= 4, "the failing observer had every event");

        gate.SetResult();
        await EventuallyAsync(() => slow.Kinds(job.Id).Contains(JobEventKind.Completed), "the slow observer caught up");
    }

    #endregion

    #region Removing, limits and data

    [Fact]
    public async Task Removing_a_job_stops_it_in_its_engine_and_forgets_it()
    {
        var engine = new FakeEngine();
        var store = new MemoryJobStore();
        var options = Options(engine);
        options.Store = store;
        var manager = await StartAsync(options);
        var job = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);
        await store.SetEngineStateAsync(job.Id, "checkpoint", "...", Token);

        await manager.RemoveAsync(job.Id, deleteData: true, Token);

        Assert.True(engine.Job(job.Id).RemovedWithData);
        Assert.Null(manager.Find(job.Id));
        Assert.Empty(await store.LoadAsync(Token));
        Assert.Null(await store.GetEngineStateAsync(job.Id, "checkpoint", Token));
    }

    [Fact]
    public async Task Removing_with_data_is_refused_when_the_engine_cant_delete_it()
    {
        var engine = new FakeEngine { Capabilities = new EngineCapabilities { Pause = true } };
        var manager = await StartAsync(Options(engine));
        var job = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);

        await Assert.ThrowsAsync<NotSupportedException>(() => manager.RemoveAsync(job.Id, deleteData: true, Token));

        Assert.NotNull(manager.Find(job.Id));
        Assert.Null(engine.Job(job.Id).RemovedWithData);
    }

    [Fact]
    public async Task Limits_reach_the_running_job_and_the_engines()
    {
        var engine = new FakeEngine();
        var manager = await StartAsync(Options(engine));
        var job = await manager.AddAsync(Link(1), Destination, limits: new JobLimits { MaxConnections = 2 }, cancellationToken: Token);
        Assert.Equal(2, engine.Job(job.Id).Request.Limits.MaxConnections);

        await manager.SetJobLimitsAsync(job.Id, new JobLimits { MaxBytesPerSecond = 1_000 }, Token);
        await manager.SetEngineLimitsAsync(new EngineLimits { MaxBytesPerSecond = 5_000 }, Token);

        Assert.Equal(1_000, engine.Job(job.Id).Limits!.MaxBytesPerSecond);
        Assert.Equal(1_000, manager.Find(job.Id)!.Limits.MaxBytesPerSecond);
        Assert.Equal(5_000, engine.Limits!.MaxBytesPerSecond);
    }

    [Fact]
    public async Task Labels_and_data_are_kept_with_the_job_and_reported()
    {
        var engine = new FakeEngine();
        var store = new MemoryJobStore();
        var observer = new RecordingObserver();
        var options = Options(engine);
        options.Store = store;
        options.Observers.Add(observer);
        var manager = await StartAsync(options);
        var job = await manager.AddAsync(Link(1), Destination, labels: ["work"], cancellationToken: Token);

        await manager.SetDataAsync(job.Id, "myplugin.note", "hello", Token);
        await manager.SetLabelsAsync(job.Id, ["work", "urgent"], Token);

        var stored = Assert.Single(await store.LoadAsync(Token)).Job;
        Assert.Equal("hello", stored.Data["myplugin.note"]);
        Assert.Equal(["urgent", "work"], stored.Labels.Order());
        await EventuallyAsync(() => observer.Kinds(job.Id).Count(kind => kind == JobEventKind.DataChanged) == 2, "both changes were reported");

        await manager.SetDataAsync(job.Id, "myplugin.note", null, Token);
        Assert.False(manager.Find(job.Id)!.Data.ContainsKey("myplugin.note"));
    }

    #endregion

    #region Restarts

    [Fact]
    public async Task After_a_restart_running_jobs_are_reattached_and_continue_while_paused_and_completed_ones_stay()
    {
        var engine = new FakeEngine();
        var store = new MemoryJobStore();
        var options = Options(engine);
        options.Store = store;
        var manager = await StartAsync(options);
        var running = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);
        var paused = await manager.AddAsync(Link(2), Destination, cancellationToken: Token);
        var completed = await manager.AddAsync(Link(3), Destination, cancellationToken: Token);
        engine.Job(running.Id).Transfer(300);
        await manager.PauseAsync(paused.Id, Token);
        engine.Job(completed.Id).Complete();
        await StateAsync(manager, completed.Id, JobState.Completed);
        await StateAsync(manager, running.Id, JobState.Transferring);
        await manager.DisposeAsync();

        var again = Options(engine);
        again.Store = store;
        var restarted = await StartAsync(again);

        await StateAsync(restarted, running.Id, JobState.Transferring);
        Assert.Equal(1, engine.Job(running.Id).Attached);
        Assert.Equal(1, engine.Job(running.Id).Resumes);
        Assert.Equal(JobState.Paused, restarted.Find(paused.Id)!.State);
        Assert.Equal(JobState.Completed, restarted.Find(completed.Id)!.State);
        Assert.Equal(0, engine.Job(completed.Id).Attached);
        Assert.Equal([running.Id, paused.Id, completed.Id], restarted.Jobs.Select(job => job.Id));
    }

    [Fact]
    public async Task A_job_its_engine_forgot_starts_over_and_one_without_its_engine_fails()
    {
        var engine = new FakeEngine();
        var gone = new FakeEngine("gone", "gone");
        var store = new MemoryJobStore();
        var options = Options(engine, gone);
        options.Store = store;
        var manager = await StartAsync(options);
        var forgotten = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);
        var orphan = await manager.AddAsync(new Uri("gone://host/a"), Destination, cancellationToken: Token);
        await manager.DisposeAsync();

        engine.ForgetJobs = true;
        var again = Options(engine);
        again.Store = store;
        var restarted = await StartAsync(again);

        await StateAsync(restarted, forgotten.Id, JobState.Preparing);
        Assert.Equal(2, engine.Jobs.Count(job => job.Request.JobId == forgotten.Id));
        var failed = restarted.Find(orphan.Id)!;
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Equal(TransferErrorKind.Unsupported, failed.Error!.Kind);
    }

    [Fact]
    public async Task Completion_steps_interrupted_by_a_restart_go_on_without_repeating_finished_ones()
    {
        var engine = new FakeEngine();
        var store = new MemoryJobStore();
        var first = new RecordingStep("first");
        var second = new RecordingStep("second") { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var options = Options(engine);
        options.Store = store;
        options.CompletionSteps.Add(first);
        options.CompletionSteps.Add(second);
        var manager = await StartAsync(options);
        var job = await manager.AddAsync(Link(1), Destination, cancellationToken: Token);
        engine.Job(job.Id).Complete();
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await manager.DisposeAsync();

        var firstAgain = new RecordingStep("first");
        var secondAgain = new RecordingStep("second");
        var again = Options(engine);
        again.Store = store;
        again.CompletionSteps.Add(firstAgain);
        again.CompletionSteps.Add(secondAgain);
        var restarted = await StartAsync(again);

        await StateAsync(restarted, job.Id, JobState.Completed);
        Assert.Equal(0, firstAgain.Runs);
        Assert.Equal(1, secondAgain.Runs);
    }

    #endregion
}
