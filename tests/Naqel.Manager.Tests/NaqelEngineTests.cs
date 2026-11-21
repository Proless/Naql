using Naqel.Files;
using Naqel.Manager.Tests.Fakes;
using Naqel.Plugins;

namespace Naqel.Manager.Tests;

/// <summary>
/// Naqel's own engine behind the manager: jobs of one file or a folder, pausing and resuming, restarts, failures and
/// limits, with real files.
/// </summary>
public sealed class NaqelEngineTests : IAsyncDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "NaqelManagerTests", Guid.NewGuid().ToString("N"));
    private readonly List<JobManager> _managers = [];
    private readonly SlowResolver _slow = new();
    private readonly MemoryJobStore _store = new();

    public NaqelEngineTests()
    {
        Directory.CreateDirectory(_temp);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Uri Output => new(Path.Combine(_temp, "out") + Path.DirectorySeparatorChar);

    public async ValueTask DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            await manager.DisposeAsync();
        }
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // A file may still be open on a failing test, don't hide the real failure
        }
    }

    private async Task<JobManager> StartAsync(TransferOptions? transfers = null)
    {
        var engineOptions = new NaqelEngineOptions
        {
            TransferOptions = transfers ?? new TransferOptions
            {
                BufferSize = 1_000,
                MinSplitPartSize = 1_000,
                ProgressInterval = TimeSpan.FromMilliseconds(10),
                Retry = new RetryPolicy { MaxRetries = 0 }
            }
        };
        engineOptions.SourceResolvers.Add(_slow);
        engineOptions.SourceResolvers.Add(new FileSourceResolver());
        engineOptions.TargetResolvers.Add(new FileTargetResolver());
        var options = new JobManagerOptions { Store = _store };
        options.Engines.Add(new NaqelEngine(engineOptions));
        var manager = new JobManager(options);
        _managers.Add(manager);
        await manager.StartAsync(Token);
        return manager;
    }

    private static async Task<JobInfo> EventuallyAsync(JobManager manager, string jobId, Func<JobInfo, bool> condition, string because)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var job = manager.Find(jobId)!;
            if (condition(job))
            {
                return job;
            }
            try
            {
                await Task.Delay(5, timeout.Token);
            }
            catch (OperationCanceledException) when (!Token.IsCancellationRequested)
            {
                Assert.Fail($"Timed out waiting until {because}; the job is {job.State} with {job.Progress.Transferred} bytes, {job.Error?.Message}");
            }
        }
    }

    private static Task<JobInfo> StateAsync(JobManager manager, string jobId, JobState state) =>
        EventuallyAsync(manager, jobId, job => job.State == state, $"the job is {state}");

    private static byte[] Content(int length, int seed)
    {
        var content = new byte[length];
        new Random(seed).NextBytes(content);
        return content;
    }

    private SlowSource Slow(string name, int length, int seed = 1, int delayMs = 2)
    {
        var source = new SlowSource(Content(length, seed), name, TimeSpan.FromMilliseconds(delayMs));
        _slow.Sources[name] = source;
        return source;
    }

    [Fact]
    public async Task A_file_is_copied_into_the_destination_folder()
    {
        var source = Path.Combine(_temp, "report.pdf");
        var content = Content(5_000, 1);
        File.WriteAllBytes(source, content);
        var manager = await StartAsync();

        var job = await manager.AddAsync(new Uri(source), Output, cancellationToken: Token);
        var done = await StateAsync(manager, job.Id, JobState.Completed);

        var copy = Path.Combine(_temp, "out", "report.pdf");
        Assert.Equal(content, File.ReadAllBytes(copy));
        var file = Assert.Single(done.Progress.Files);
        Assert.Equal("report.pdf", file.Name);
        Assert.True(file.Done);
        Assert.Equal(copy, file.Location);
        Assert.Equal(5_000, done.Progress.Total);
        Assert.Equal(5_000, done.Progress.Transferred);
        Assert.Equal(NaqelEngine.EngineName, done.Engine);
    }

    [Fact]
    public async Task A_folder_is_one_job_with_a_file_for_each_of_its_files()
    {
        var folder = Path.Combine(_temp, "in");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllBytes(Path.Combine(folder, "a.txt"), Content(100, 1));
        File.WriteAllBytes(Path.Combine(folder, "sub", "b.txt"), Content(2_000, 2));
        var manager = await StartAsync();

        var job = await manager.AddAsync(new Uri(folder), Output, cancellationToken: Token);
        var done = await StateAsync(manager, job.Id, JobState.Completed);

        Assert.Equal(["a.txt", "sub/b.txt"], done.Progress.Files.Select(file => file.Path).Order(StringComparer.Ordinal));
        Assert.All(done.Progress.Files, file => Assert.True(file.Done));
        Assert.Equal(2_100, done.Progress.Total);
        Assert.Equal(Content(2_000, 2), File.ReadAllBytes(Path.Combine(_temp, "out", "sub", "b.txt")));
    }

    [Fact]
    public async Task A_paused_job_continues_where_it_stopped()
    {
        var source = Slow("big.bin", 200_000);
        var manager = await StartAsync();
        var job = await manager.AddAsync(new Uri("slow:big.bin"), Output, cancellationToken: Token);
        await EventuallyAsync(manager, job.Id, job => job.Progress.Transferred > 20_000, "some bytes arrived");

        await manager.PauseAsync(job.Id, Token);
        await Task.Delay(100, Token);
        Assert.Equal(JobState.Paused, manager.Find(job.Id)!.State);

        await manager.ResumeAsync(job.Id, Token);
        await StateAsync(manager, job.Id, JobState.Completed);

        Assert.Equal(source.Content, File.ReadAllBytes(Path.Combine(_temp, "out", "big.bin")));
        // The second run asked only for what the first hadn't kept: more than 20 KB had arrived before the pause, and
        // the checkpoint trails what arrived by at most one read
        Assert.Equal(2, source.Reads.Count);
        Assert.True(source.Reads[1].Start >= 19_000, $"Continued from {source.Reads[1].Start}");
    }

    [Fact]
    public async Task After_a_restart_a_job_continues_from_its_checkpoint_and_skips_finished_files()
    {
        var first = Slow("a.bin", 5_000, seed: 1, delayMs: 0);
        var second = Slow("b.bin", 200_000, seed: 2);
        var manager = await StartAsync();
        var job = await manager.AddAsync(new Uri("slow:folder/"), Output, cancellationToken: Token);
        await EventuallyAsync(manager, job.Id, job => job.Progress.Files.Count == 2 && job.Progress.Files[1].Transferred > 20_000, "the second file is under way");
        var firstReads = first.Reads.Count;
        await manager.DisposeAsync();

        var restarted = await StartAsync();
        await StateAsync(restarted, job.Id, JobState.Completed);

        Assert.Equal(first.Content, File.ReadAllBytes(Path.Combine(_temp, "out", "a.bin")));
        Assert.Equal(second.Content, File.ReadAllBytes(Path.Combine(_temp, "out", "b.bin")));
        Assert.Equal(firstReads, first.Reads.Count);
        Assert.Contains(second.Reads, range => range.Start > 0);
    }

    [Fact]
    public async Task A_missing_source_fails_the_job_with_its_kind_of_error()
    {
        var manager = await StartAsync();

        var job = await manager.AddAsync(new Uri("slow:nothing.bin"), Output, cancellationToken: Token);
        var failed = await StateAsync(manager, job.Id, JobState.Failed);

        Assert.Equal(TransferErrorKind.NotFound, failed.Error!.Kind);
    }

    [Fact]
    public async Task A_jobs_speed_limit_slows_its_transfer()
    {
        Slow("limited.bin", 60_000, delayMs: 0);
        var manager = await StartAsync();

        var job = await manager.AddAsync(new Uri("slow:limited.bin"), Output, limits: new JobLimits { MaxBytesPerSecond = 100_000 }, cancellationToken: Token);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await StateAsync(manager, job.Id, JobState.Completed);

        // 60 KB at 100 KB/s, minus the 10 KB the limiter lets through at once, takes at least half a second
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(400), $"Took only {watch.Elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task Lifting_a_running_jobs_limit_lets_it_finish_at_once()
    {
        Slow("lifted.bin", 200_000, delayMs: 0);
        var manager = await StartAsync();
        // 200 KB at 20 KB/s would take ten seconds
        var job = await manager.AddAsync(new Uri("slow:lifted.bin"), Output, limits: new JobLimits { MaxBytesPerSecond = 20_000 }, cancellationToken: Token);
        await EventuallyAsync(manager, job.Id, job => job.Progress.Transferred > 0, "the transfer started");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await manager.SetJobLimitsAsync(job.Id, JobLimits.None, Token);
        await StateAsync(manager, job.Id, JobState.Completed);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"Took {watch.Elapsed.TotalMilliseconds} ms after lifting the limit");
    }
}
