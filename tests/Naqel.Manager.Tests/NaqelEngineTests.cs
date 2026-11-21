using Naqel.Files;
using Naqel.Manager.Tests.Fakes;
using Naqel.Plugins;
using Naqel.Storage;

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
    private readonly StorageLocations _locations = new();

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
        engineOptions.SourceResolvers.Add(_locations);
        engineOptions.SourceResolvers.Add(_slow);
        engineOptions.SourceResolvers.Add(new FileSourceResolver());
        engineOptions.TargetResolvers.Add(_locations);
        engineOptions.TargetResolvers.Add(new FileTargetResolver());
        var options = new JobManagerOptions { Store = _store, Locations = _locations };
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

    #region Storage locations and deleting data

    private string Location(string name, WriteMode mode = WriteMode.Overwrite)
    {
        var root = Path.Combine(_temp, name);
        _locations.Add(name, new FileStorage(root), mode);
        return root;
    }

    private string Folder(params (string Path, int Length)[] files)
    {
        var folder = Path.Combine(_temp, "in");
        foreach (var (path, length) in files)
        {
            var full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, Content(length, path.Length));
        }
        return folder;
    }

    private static string[] FilesIn(string folder) =>
        Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal)]
            : [];

    [Fact]
    public async Task A_job_saves_into_a_storage_location_by_its_name()
    {
        var root = Location("media");
        var folder = Folder(("a.txt", 100), ("sub/b.txt", 2_000));
        var manager = await StartAsync();

        var job = await manager.AddAsync(new Uri(folder), StorageLocations.UriOf("media", "backup"), cancellationToken: Token);
        await StateAsync(manager, job.Id, JobState.Completed);

        Assert.Equal(["backup/a.txt", "backup/sub/b.txt"], FilesIn(root));
        Assert.Same(_locations, manager.Locations);
    }

    [Fact]
    public async Task Removing_a_completed_job_with_its_data_deletes_its_files_from_the_location()
    {
        var root = Location("media");
        var folder = Folder(("a.txt", 100), ("sub/b.txt", 2_000));
        File.WriteAllBytes(Path.Combine(_temp, "media-other.txt"), [1]);
        var manager = await StartAsync();
        var job = await manager.AddAsync(new Uri(folder), StorageLocations.UriOf("media"), cancellationToken: Token);
        await StateAsync(manager, job.Id, JobState.Completed);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "not-this-jobs.txt"), [1]);

        await manager.RemoveAsync(job.Id, deleteData: true, Token);

        Assert.Equal(["not-this-jobs.txt"], FilesIn(root));
        Assert.Null(manager.Find(job.Id));
    }

    [Fact]
    public async Task Removing_an_unfinished_job_with_its_data_discards_what_its_target_kept()
    {
        Slow("big.bin", 200_000);
        var manager = await StartAsync();
        var job = await manager.AddAsync(new Uri("slow:big.bin"), Output, cancellationToken: Token);
        await EventuallyAsync(manager, job.Id, job => job.Progress.Transferred > 20_000, "some bytes arrived");
        await manager.PauseAsync(job.Id, Token);
        var output = Path.Combine(_temp, "out");
        Assert.Contains("big.bin" + FileTarget.PartialExtension, FilesIn(output));

        await manager.RemoveAsync(job.Id, deleteData: true, Token);

        Assert.Empty(FilesIn(output));
        Assert.Null(await _store.GetEngineStateAsync(job.Id, "checkpoint:", Token));
    }

    [Fact]
    public async Task Removing_a_job_without_its_data_keeps_its_files()
    {
        var root = Location("media");
        var folder = Folder(("a.txt", 100));
        var manager = await StartAsync();
        var job = await manager.AddAsync(new Uri(folder), StorageLocations.UriOf("media"), cancellationToken: Token);
        await StateAsync(manager, job.Id, JobState.Completed);

        await manager.RemoveAsync(job.Id, cancellationToken: Token);

        Assert.Equal(["a.txt"], FilesIn(root));
    }

    [Fact]
    public async Task Finished_files_outside_a_storage_or_written_beside_others_are_kept()
    {
        var keepBoth = Location("shared", WriteMode.KeepBoth);
        Directory.CreateDirectory(keepBoth);
        File.WriteAllBytes(Path.Combine(keepBoth, "a.txt"), [42]);
        var folder = Folder(("a.txt", 100));
        var manager = await StartAsync();
        var plain = await manager.AddAsync(new Uri(folder), Output, cancellationToken: Token);
        var beside = await manager.AddAsync(new Uri(folder), StorageLocations.UriOf("shared"), cancellationToken: Token);
        await StateAsync(manager, plain.Id, JobState.Completed);
        await StateAsync(manager, beside.Id, JobState.Completed);

        await manager.RemoveAsync(plain.Id, deleteData: true, Token);
        await manager.RemoveAsync(beside.Id, deleteData: true, Token);

        // A plain folder isn't a storage that can delete, and a write that kept both can't tell its file by its path
        Assert.Equal(["a.txt"], FilesIn(Path.Combine(_temp, "out")));
        Assert.Equal(["a (1).txt", "a.txt"], FilesIn(keepBoth));
        Assert.Equal([42], File.ReadAllBytes(Path.Combine(keepBoth, "a.txt")));
    }

    [Fact]
    public async Task A_single_file_its_resolver_didnt_describe_goes_into_a_location_under_its_own_name()
    {
        var root = Location("media");
        var source = Slow("named.bin", 3_000, delayMs: 0);
        _slow.Describe = false;
        var manager = await StartAsync();

        var job = await manager.AddAsync(new Uri("slow:named.bin"), StorageLocations.UriOf("media"), cancellationToken: Token);
        await StateAsync(manager, job.Id, JobState.Completed);

        Assert.Equal(source.Content, File.ReadAllBytes(Path.Combine(root, "named.bin")));
    }

    [Fact]
    public async Task A_job_into_a_location_that_was_removed_fails_as_not_found()
    {
        Location("gone");
        var folder = Folder(("a.txt", 100));
        _locations.Remove("gone");
        var manager = await StartAsync();

        var job = await manager.AddAsync(new Uri(folder), StorageLocations.UriOf("gone"), cancellationToken: Token);
        var failed = await StateAsync(manager, job.Id, JobState.Failed);

        Assert.Equal(TransferErrorKind.NotFound, failed.Error!.Kind);
    }

    #endregion
}
