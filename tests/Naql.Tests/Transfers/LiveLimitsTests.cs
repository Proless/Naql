using Naql.Engine;
using Naql.Tests.Fakes;

namespace Naql.Tests.Transfers;

/// <summary>
/// Changing a transfer's speed limit and connection count before and while it runs.
/// </summary>
public sealed class LiveLimitsTests
{
    private static readonly TransferOptions Options = new()
    {
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false },
        MinSplitPartSize = 1_000,
        BufferSize = 1_000,
        ResponseTimeout = TimeSpan.FromSeconds(10),
        IdleTimeout = TimeSpan.FromSeconds(10),
        ProgressInterval = TimeSpan.FromMilliseconds(10)
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        new Random(42).NextBytes(content);
        return content;
    }

    // Eight parts of 1,000 bytes, more than the connections, so some wait for one
    private static MemoryTarget EightParts(Func<int, ByteRange, CancellationToken, Task> onWrite) => new()
    {
        Capabilities = new TargetCapabilities { ParallelParts = true, ResumableParts = true, Resumable = true, MaxPartSize = 1_000 },
        OnWrite = onWrite
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    [Fact]
    public async Task Lifting_the_speed_limit_while_the_transfer_runs_lets_it_finish_at_once()
    {
        var content = Content(200_000);
        var target = new MemoryTarget();
        // 200 KB at 20 KB/s would take ten seconds
        var operation = new TransferClient(Options with { MaxBytesPerSecond = 20_000 }).From(new MemorySource(content)).To(target).Build();
        long transferred = 0;
        var progress = new SynchronousProgress(p => Interlocked.Exchange(ref transferred, p.Transferred));

        var run = operation.RunAsync(progress, Token);
        await WaitUntilAsync(() => Interlocked.Read(ref transferred) > 0);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        operation.SetMaxBytesPerSecond(0);
        await run;

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(0, operation.MaxBytesPerSecond);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Took {watch.Elapsed.TotalMilliseconds} ms after lifting the limit");
    }

    [Fact]
    public async Task A_speed_limit_set_before_the_run_applies_to_it()
    {
        var content = Content(200_000);
        var target = new MemoryTarget();
        var operation = new TransferClient(Options).From(new MemorySource(content)).To(target).Build();

        operation.SetMaxBytesPerSecond(400_000);
        var result = await operation.RunAsync(cancellationToken: Token);

        // 200 KB at 400 KB/s, minus the 40 KB the limiter lets through at once, takes at least 0.4 s
        Assert.Equal(content, target.Session.Data);
        Assert.Equal(400_000, operation.MaxBytesPerSecond);
        Assert.True(result.Duration >= TimeSpan.FromMilliseconds(350), $"Took only {result.Duration.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task More_connections_while_the_transfer_runs_start_waiting_parts_at_once()
    {
        var content = Content(8_000);
        var started = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = EightParts(async (_, _, ct) =>
        {
            Interlocked.Increment(ref started);
            await release.Task.WaitAsync(ct);
        });
        var operation = new TransferClient(Options with { MaxConnections = 2 }).From(new MemorySource(content)).To(target).Build();

        var run = operation.RunAsync(cancellationToken: Token);
        await WaitUntilAsync(() => Volatile.Read(ref started) == 2);
        await Task.Delay(50, Token);
        Assert.Equal(2, Volatile.Read(ref started));

        operation.SetMaxConnections(4);
        await WaitUntilAsync(() => Volatile.Read(ref started) == 4);
        release.TrySetResult(true);
        var result = await run;

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(8, result.Parts);
        Assert.Equal(4, target.Session.MaxActiveWrites);
        Assert.Equal(4, operation.MaxConnections);
    }

    [Fact]
    public async Task Fewer_connections_while_the_transfer_runs_let_running_parts_finish_and_start_fewer()
    {
        var content = Content(8_000);
        var started = 0;
        var late = 0;
        var maxLate = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = EightParts(async (index, _, ct) =>
        {
            Interlocked.Increment(ref started);
            if (index < 4)
            {
                await release.Task.WaitAsync(ct);
                return;
            }
            var now = Interlocked.Increment(ref late);
            InterlockedMax(ref maxLate, now);
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref late);
        });
        var operation = new TransferClient(Options with { MaxConnections = 4 }).From(new MemorySource(content)).To(target).Build();

        var run = operation.RunAsync(cancellationToken: Token);
        await WaitUntilAsync(() => Volatile.Read(ref started) == 4);
        operation.SetMaxConnections(1);
        release.TrySetResult(true);
        var result = await run;

        // The four running parts weren't stopped, and the other four ran one at a time
        Assert.Equal(content, target.Session.Data);
        Assert.Equal(0, result.Retries);
        Assert.Equal(8, target.Session.Writes.Count);
        Assert.Equal(1, maxLate);
    }

    [Fact]
    public async Task Connections_set_before_the_run_decide_how_its_parts_are_planned()
    {
        var content = Content(10_000);
        var target = new MemoryTarget();
        var operation = new TransferClient(Options).From(new MemorySource(content)).To(target).Build();

        operation.SetMaxConnections(4);
        var result = await operation.RunAsync(cancellationToken: Token);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(4, result.Parts);
    }

    [Fact]
    public async Task The_source_limit_still_applies_after_raising_the_connections()
    {
        var content = Content(8_000);
        var started = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = EightParts(async (_, _, ct) =>
        {
            Interlocked.Increment(ref started);
            await release.Task.WaitAsync(ct);
        });
        var source = new MemorySource(content) { MaxConnections = 2 };
        var operation = new TransferClient(Options with { MaxConnections = 4 }).From(source).To(target).Build();

        var run = operation.RunAsync(cancellationToken: Token);
        await WaitUntilAsync(() => Volatile.Read(ref started) == 2);
        operation.SetMaxConnections(8);
        await Task.Delay(50, Token);
        Assert.Equal(2, Volatile.Read(ref started));
        release.TrySetResult(true);
        await run;

        Assert.Equal(2, target.Session.MaxActiveWrites);
    }

    [Fact]
    public void Invalid_limits_are_rejected()
    {
        var operation = new TransferClient(Options).From(new MemorySource([1])).To(new MemoryTarget()).Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => operation.SetMaxBytesPerSecond(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => operation.SetMaxConnections(0));
    }

    [Fact]
    public async Task A_canceled_wait_for_a_connection_doesnt_take_one()
    {
        var gate = new ConnectionGate(1);
        await gate.WaitAsync(Token);
        using var canceled = new CancellationTokenSource();
        var waiting = gate.WaitAsync(canceled.Token);
        var next = gate.WaitAsync(Token);

        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        gate.Release();

        await next.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(1, gate.Active);
        Assert.Equal(0, gate.Waiting);
    }

    [Fact]
    public async Task Raising_the_gate_limit_admits_waiters_in_order_and_tells_listeners()
    {
        var gate = new ConnectionGate(1);
        var freed = 0;
        gate.Freed += () => Interlocked.Increment(ref freed);
        await gate.WaitAsync(Token);
        var second = gate.WaitAsync(Token);
        var third = gate.WaitAsync(Token);

        gate.Limit = 2;

        await second.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(third.IsCompleted);
        Assert.Equal(1, freed);
        gate.Release();
        await third.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(2, freed);
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int current;
        while ((current = Volatile.Read(ref location)) < value && Interlocked.CompareExchange(ref location, value, current) != current)
        {
        }
    }

    private sealed class SynchronousProgress(Action<TransferProgress> handler) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => handler(value);
    }
}
