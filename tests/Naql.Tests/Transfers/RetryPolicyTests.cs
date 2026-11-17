namespace Naql.Tests.Transfers;

public sealed class RetryPolicyTests
{
    [Fact]
    public void Delays_grow_by_the_factor_up_to_the_maximum()
    {
        var policy = new RetryPolicy { InitialDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(5), Jitter = false };

        var delays = Enumerable.Range(1, 5).Select(retry => policy.GetDelay(retry).TotalSeconds);

        Assert.Equal(new double[] { 1, 2, 4, 5, 5 }, delays);
    }

    [Fact]
    public void Jitter_varies_the_delay_by_up_to_a_fifth()
    {
        var policy = new RetryPolicy { InitialDelay = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromSeconds(60) };

        for (var i = 0; i < 100; i++)
        {
            Assert.InRange(policy.GetDelay(1).TotalSeconds, 8, 12);
        }
    }

    [Fact]
    public void A_longer_retry_after_wins()
    {
        var policy = new RetryPolicy { InitialDelay = TimeSpan.FromSeconds(1), Jitter = false };

        Assert.Equal(TimeSpan.FromMinutes(2), policy.GetDelay(1, TimeSpan.FromMinutes(2)));
        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(1, TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void None_never_retries()
    {
        Assert.Equal(0, RetryPolicy.None.MaxRetries);
    }

    [Fact]
    public void Rejects_invalid_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { MaxRetries = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { InitialDelay = TimeSpan.FromSeconds(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { BackoffFactor = 0.5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.Default.GetDelay(0));
    }
}
