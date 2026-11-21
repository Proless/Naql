using Naqel.Plugins;

namespace Naqel.Manager.Tests.Fakes;

/// <summary>
/// An engine whose jobs the test drives: they report what the test tells them to. It keeps its jobs when the manager
/// disposes it, so a new manager can attach them again, like an engine that keeps its own state.
/// </summary>
public sealed class FakeEngine(string name = "fake", string scheme = "fake") : ITransferEngine
{
    private readonly Lock _lock = new();
    private readonly List<FakeJob> _jobs = [];
    private int _ids;

    public string Name { get; } = name;

    public EngineCapabilities Capabilities { get; init; } = new() { Pause = true, JobLimits = true, EngineLimits = true, DeleteData = true, Seeding = true };

    public IEngineHost? Host { get; private set; }

    public EngineLimits? Limits { get; private set; }

    /// <summary>Makes AddAsync fail with this, if set.</summary>
    public Exception? AddFailure { get; set; }

    /// <summary>Makes AttachAsync forget every job, like an engine that lost its state.</summary>
    public bool ForgetJobs { get; set; }

    public int Starts { get; private set; }

    public IReadOnlyList<FakeJob> Jobs
    {
        get
        {
            lock (_lock)
            {
                return [.. _jobs];
            }
        }
    }

    public FakeJob Job(string jobId) => Jobs.Single(job => job.Request.JobId == jobId);

    public bool CanHandle(Uri reference) => reference.Scheme == scheme;

    public Task StartAsync(IEngineHost host, CancellationToken cancellationToken)
    {
        Host = host;
        Starts++;
        return Task.CompletedTask;
    }

    public Task<IEngineJob> AddAsync(JobRequest request, CancellationToken cancellationToken)
    {
        if (AddFailure != null)
        {
            throw AddFailure;
        }
        var job = new FakeJob(this, request, $"{Name}-{Interlocked.Increment(ref _ids)}");
        lock (_lock)
        {
            _jobs.Add(job);
        }
        job.Report(new JobSnapshot { State = JobState.Preparing });
        return Task.FromResult<IEngineJob>(job);
    }

    public Task<IEngineJob?> AttachAsync(JobRequest request, string engineJobId, CancellationToken cancellationToken)
    {
        var job = ForgetJobs ? null : Jobs.FirstOrDefault(job => job.Id == engineJobId);
        if (job != null)
        {
            job.Attached++;
            job.Request = request;
            job.SetSilently(job.Snapshot with { State = JobState.Paused });
        }
        return Task.FromResult<IEngineJob?>(job);
    }

    public Task SetLimitsAsync(EngineLimits limits, CancellationToken cancellationToken)
    {
        Limits = limits;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => default;
}

/// <summary>
/// A job of the fake engine, which reports whatever the test makes it do.
/// </summary>
public sealed class FakeJob(FakeEngine engine, JobRequest request, string id) : IEngineJob
{
    private volatile JobSnapshot _snapshot = JobSnapshot.Empty;

    public string Id { get; } = id;

    public JobRequest Request { get; set; } = request;

    public JobSnapshot Snapshot => _snapshot;

    public int Pauses { get; private set; }

    public int Resumes { get; private set; }

    public int Attached { get; set; }

    public bool? RemovedWithData { get; private set; }

    public JobLimits? Limits { get; private set; }

    public void Report(JobSnapshot snapshot)
    {
        _snapshot = snapshot;
        engine.Host!.Report(Request.JobId, snapshot);
    }

    public void SetSilently(JobSnapshot snapshot) => _snapshot = snapshot;

    public void Transfer(long transferred, long total = 1_000) =>
        Report(new JobSnapshot { State = JobState.Transferring, Transferred = transferred, Total = total });

    public void Complete(bool seeding = false) => Report(new JobSnapshot
    {
        State = seeding ? JobState.Seeding : JobState.Completed,
        Transferred = 1_000,
        Total = 1_000,
        ContentComplete = true,
        Files = [new JobFile("", "file.bin", 1_000, 1_000, Done: true, Location: "/downloads/file.bin")]
    });

    public void Fail(TransferErrorKind kind = TransferErrorKind.Network) =>
        Report(_snapshot with { State = JobState.Failed, Error = new JobError(kind, "It broke", IsTransient: true) });

    /// <summary>When set, pausing waits for it before it reports, as an engine does that stops a run first.</summary>
    public Task? PauseGate { get; set; }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        Pauses++;
        if (PauseGate is { } gate)
        {
            await gate;
        }
        Report(_snapshot with { State = JobState.Paused });
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        Resumes++;
        Report(_snapshot with { State = _snapshot.ContentComplete ? JobState.Seeding : JobState.Transferring, Error = null });
        return Task.CompletedTask;
    }

    public Task RemoveAsync(bool deleteData, CancellationToken cancellationToken)
    {
        RemovedWithData = deleteData;
        return Task.CompletedTask;
    }

    public Task SetLimitsAsync(JobLimits limits, CancellationToken cancellationToken)
    {
        Limits = limits;
        return Task.CompletedTask;
    }
}
