using System.Diagnostics;

namespace Naql.Tests.Transfers;

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
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandwidthLimiter(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new BandwidthLimiter(1).WaitAsync(-1, Token); });
    }
}
