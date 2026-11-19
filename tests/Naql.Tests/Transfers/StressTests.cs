using Naql.Tests.Fakes;

namespace Naql.Tests.Transfers;

/// <summary>
/// Many transfers at once, sharing a speed limit, some failing and some canceled midway.
/// </summary>
public sealed class StressTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Many_transfers_at_once_each_end_on_their_own_terms_and_clean_up()
    {
        using var leaks = new Leaks();
        var limiter = new BandwidthLimiter(20_000_000);
        var client = new TransferClient(new TransferOptions
        {
            MaxConnections = 4,
            MinSplitPartSize = 2_000,
            BufferSize = 1_000,
            Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.FromMilliseconds(1), Jitter = true }
        }, limiter);
        var random = new Random(17);

        var runs = Enumerable.Range(0, 40).Select(async i =>
        {
            var content = new byte[random.Next(0, 60_000)];
            random.NextBytes(content);
            // Every third transfer has a flaky connection, every fifth is canceled midway
            var flaky = i % 3 == 0;
            var source = new MemorySource(content)
            {
                WrapContent = (attempt, _, stream) => leaks.Track(flaky && attempt < 2
                    ? new FailingStream(stream, 1_500, () => new IOException("The connection was reset"))
                    : stream)
            };
            var target = new MemoryTarget();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
            if (i % 5 == 0)
            {
                cts.CancelAfter(5 + i);
            }

            try
            {
                await client.From(source).To(target).RunAsync(cancellationToken: cts.Token);
                Assert.Equal(content, Assert.Single(target.Sessions, session => session.Completed).Data);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                Assert.DoesNotContain(target.Sessions, session => session.Completed);
            }
            Assert.All(target.Sessions, session => Assert.True(session.Disposed));
        }).ToList();

        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(60), Token);

        Assert.Equal(0, leaks.OpenStreams);
        Assert.Empty(leaks.Unobserved());
    }

    [Fact]
    public async Task Canceling_while_parts_wait_for_a_connection_ends_promptly()
    {
        using var leaks = new Leaks();
        var content = new byte[200_000];
        // 50 parts over two connections, slow enough that most are still waiting when the transfer is canceled
        var source = new MemorySource(content) { WrapContent = (_, _, stream) => leaks.Track(stream) };
        var options = new TransferOptions { MaxConnections = 2, MinSplitPartSize = 1_000, MaxBytesPerSecond = 100_000 };
        var target = new MemoryTarget { Capabilities = new() { ParallelParts = true, ResumableParts = true, Resumable = true, MaxPartSize = 4_000 } };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cts.CancelAfter(200);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TransferClient(options).From(source).To(target).RunAsync(cancellationToken: cts.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Took {clock.Elapsed} to stop");
        Assert.Equal(0, leaks.OpenStreams);
        Assert.True(target.Session.Disposed);
        Assert.Empty(leaks.Unobserved());
    }
}
