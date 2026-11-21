using System.Diagnostics;

namespace Naqel.Core.Tests.Transfers;

public sealed class BandwidthLimiterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Lets_a_tenth_of_a_second_through_without_waiting()
    {
        var limiter = new BandwidthLimiter(1_000_000);

        Assert.True(limiter.WaitAsync(100_000, Token).IsCompleted);
    }

    [Fact]
    public async Task Waits_for_bytes_beyond_the_rate()
    {
        var limiter = new BandwidthLimiter(100_000);
        await limiter.WaitAsync(10_000, Token);

        var watch = Stopwatch.StartNew();
        await limiter.WaitAsync(50_000, Token);

        // 50 KB at 100 KB/s, with some room for timer resolution
        Assert.True(watch.ElapsedMilliseconds >= 400, $"Waited only {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Rejects_invalid_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandwidthLimiter(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandwidthLimiter(1).BytesPerSecond = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new BandwidthLimiter(1).WaitAsync(-1, Token); });
    }

    [Fact]
    public void Zero_means_no_limit()
    {
        var limiter = new BandwidthLimiter(0);

        Assert.True(limiter.WaitAsync(1_000_000_000, Token).IsCompleted);
        Assert.True(limiter.WaitAsync(1_000_000_000, Token).IsCompleted);
    }

    [Fact]
    public async Task A_new_rate_applies_to_the_next_wait()
    {
        var limiter = new BandwidthLimiter(1_000);
        await limiter.WaitAsync(100, Token);

        limiter.BytesPerSecond = 0;
        Assert.True(limiter.WaitAsync(1_000_000, Token).IsCompleted);

        // From no limit to a low one: the bucket starts empty-ish, so a large read waits
        limiter.BytesPerSecond = 100_000;
        var watch = Stopwatch.StartNew();
        await limiter.WaitAsync(50_000, Token);
        Assert.True(watch.ElapsedMilliseconds >= 350, $"Waited only {watch.ElapsedMilliseconds} ms");
        Assert.Equal(100_000, limiter.BytesPerSecond);
    }
}
