using Naqel.Files;
using Naqel.Manager.Tests.Fakes;
using Naqel.Plugins;

namespace Naqel.Manager.Tests;

/// <summary>
/// Asking the user: a job waits as needing attention until the question is answered, declined or no longer waited for,
/// and accounts sign in and get their tokens through their providers.
/// </summary>
public sealed class InteractionTests : IAsyncDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "NaqelInteractionTests", Guid.NewGuid().ToString("N"));
    private readonly List<JobManager> _managers = [];
    private readonly ProtectedResolver _protected = new("open sesame");

    public InteractionTests()
    {
        Directory.CreateDirectory(_temp);
        _protected.Files["secret.bin"] = [1, 2, 3];
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
            // A file may still be open on a failing test
        }
    }

    private async Task<JobManager> StartAsync(Action<JobManagerOptions>? configure = null)
    {
        var engineOptions = new NaqelEngineOptions();
        engineOptions.SourceResolvers.Add(_protected);
        engineOptions.TargetResolvers.Add(new FileTargetResolver());
        var options = new JobManagerOptions();
        options.Engines.Add(new NaqelEngine(engineOptions));
        configure?.Invoke(options);
        var manager = new JobManager(options);
        _managers.Add(manager);
        await manager.StartAsync(Token);
        return manager;
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

    [Fact]
    public async Task A_job_waits_as_needing_attention_until_its_question_is_answered()
    {
        var observer = new RecordingObserver();
        var manager = await StartAsync(options => options.Observers.Add(observer));

        var job = await manager.AddAsync(new Uri("protected:secret.bin"), Output, cancellationToken: Token);
        await StateAsync(manager, job.Id, JobState.NeedsAttention);

        var question = Assert.Single(manager.Interactions.Pending);
        Assert.Equal(job.Id, question.Request.JobId);
        Assert.Equal(InteractionKind.Secret, question.Request.Kind);

        Assert.True(manager.Interactions.Answer(question.Id, new InteractionAnswer("open sesame")));
        await StateAsync(manager, job.Id, JobState.Completed);

        Assert.Empty(manager.Interactions.Pending);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_temp, "out", "secret.bin")));
        await EventuallyAsync(() => observer.Events.Any(e => e.Job.State == JobState.NeedsAttention), "the observer heard the job needed attention");
    }

    [Fact]
    public async Task A_host_can_answer_as_soon_as_a_question_is_asked()
    {
        var manager = await StartAsync();
        manager.Interactions.Asked += question => manager.Interactions.Answer(question.Id, new InteractionAnswer("open sesame"));

        var job = await manager.AddAsync(new Uri("protected:secret.bin"), Output, cancellationToken: Token);

        await StateAsync(manager, job.Id, JobState.Completed);
    }

    [Fact]
    public async Task A_declined_question_fails_the_job_as_its_plugin_decides()
    {
        var manager = await StartAsync();
        var job = await manager.AddAsync(new Uri("protected:secret.bin"), Output, cancellationToken: Token);
        await StateAsync(manager, job.Id, JobState.NeedsAttention);

        manager.Interactions.Answer(manager.Interactions.Pending[0].Id, InteractionAnswer.Declined);
        await StateAsync(manager, job.Id, JobState.Failed);

        Assert.Equal(TransferErrorKind.AccessDenied, manager.Find(job.Id)!.Error!.Kind);
    }

    [Fact]
    public async Task Removing_or_pausing_a_job_stops_waiting_for_its_question_and_resuming_asks_again()
    {
        var manager = await StartAsync();
        var paused = await manager.AddAsync(new Uri("protected:secret.bin"), Output, cancellationToken: Token);
        var removed = await manager.AddAsync(new Uri("protected:secret.bin"), new Uri(Path.Combine(_temp, "other") + Path.DirectorySeparatorChar), cancellationToken: Token);
        await StateAsync(manager, paused.Id, JobState.NeedsAttention);
        await StateAsync(manager, removed.Id, JobState.NeedsAttention);
        Assert.Equal(2, manager.Interactions.Pending.Count);

        await manager.RemoveAsync(removed.Id, cancellationToken: Token);
        await manager.PauseAsync(paused.Id, Token);

        await EventuallyAsync(() => manager.Interactions.Pending.Count == 0, "no question waits any more");
        Assert.Equal(JobState.Paused, manager.Find(paused.Id)!.State);

        await manager.ResumeAsync(paused.Id, Token);
        await StateAsync(manager, paused.Id, JobState.NeedsAttention);
        Assert.Equal(3, _protected.Asked);
    }

    [Fact]
    public async Task A_job_that_needs_attention_keeps_its_place_in_the_queue()
    {
        var manager = await StartAsync(options => options.MaxActiveJobs = 1);
        var first = await manager.AddAsync(new Uri("protected:secret.bin"), Output, cancellationToken: Token);
        var second = await manager.AddAsync(new Uri("protected:secret.bin"), Output, cancellationToken: Token);
        await StateAsync(manager, first.Id, JobState.NeedsAttention);

        await Task.Delay(50, Token);

        Assert.Equal(JobState.Queued, manager.Find(second.Id)!.State);
    }

    [Fact]
    public async Task An_account_signs_in_through_its_provider_and_hands_out_tokens()
    {
        var provider = new FakeAccountProvider();
        var secrets = new MemorySecretStore();
        var manager = await StartAsync(options =>
        {
            options.AccountProviders.Add(provider);
            options.Secrets = secrets;
        });
        manager.Interactions.Asked += question =>
        {
            Assert.Equal(InteractionKind.OpenUrl, question.Request.Kind);
            Assert.Null(question.Request.JobId);
            manager.Interactions.Answer(question.Id, new InteractionAnswer("code-alice"));
        };

        var account = await manager.Accounts.SignInAsync("cloud", Token);

        Assert.Equal(new AccountInfo("cloud", "alice", "alice@cloud.test"), account);
        Assert.Equal([account], manager.Accounts.OfService("cloud"));
        Assert.Equal("token-for-alice-refresh-0", await manager.Accounts.GetTokenAsync("cloud", "alice", refresh: false, Token));
        Assert.Equal("token-for-alice-refresh-1", await manager.Accounts.GetTokenAsync("cloud", "alice", refresh: true, Token));
        Assert.Equal("token-for-alice-refresh-1", await manager.Accounts.GetTokenAsync("cloud", "alice", refresh: false, Token));
    }

    [Fact]
    public async Task Accounts_survive_a_restart_and_signing_out_forgets_their_secrets()
    {
        var provider = new FakeAccountProvider();
        var secrets = new MemorySecretStore();
        var manager = await StartAsync(options =>
        {
            options.AccountProviders.Add(provider);
            options.Secrets = secrets;
        });
        manager.Interactions.Asked += question => manager.Interactions.Answer(question.Id, new InteractionAnswer("code-bob"));
        await manager.Accounts.SignInAsync("cloud", Token);
        await manager.DisposeAsync();

        var restarted = await StartAsync(options =>
        {
            options.AccountProviders.Add(provider);
            options.Secrets = secrets;
        });
        Assert.Equal(["bob"], restarted.Accounts.All.Select(account => account.Id));

        await restarted.Accounts.SignOutAsync("cloud", "bob", Token);

        Assert.Empty(restarted.Accounts.All);
        Assert.Single(provider.SignedOut);
        Assert.Null(await secrets.GetAsync("naqel.account:cloud:bob", "refresh", Token));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => restarted.Accounts.GetTokenAsync("cloud", "bob", refresh: false, Token));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => restarted.Accounts.SignInAsync("unknown", Token));
    }
}
