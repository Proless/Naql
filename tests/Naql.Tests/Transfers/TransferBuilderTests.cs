using Naql.Tests.Fakes;

namespace Naql.Tests.Transfers;

public sealed class TransferBuilderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Transfer_From_runs_with_the_default_client()
    {
        var content = new byte[] { 1, 2, 3 };
        var target = new MemoryTarget();

        var result = await Transfer.From(new MemorySource(content)).To(target).RunAsync(cancellationToken: Token);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(3, result.Transferred);
    }

    [Fact]
    public void The_builder_starts_from_the_client_defaults_and_changes_only_what_is_set()
    {
        var defaults = new TransferOptions { BufferSize = 123 };
        var retry = new RetryPolicy { MaxRetries = 7 };

        var operation = new TransferClient(defaults)
            .From(new MemorySource([]))
            .To(new MemoryTarget())
            .WithMaxConnections(4)
            .WithMaxRate(1_000)
            .WithRetry(retry)
            .Build();

        Assert.Equal(defaults with { MaxConnections = 4, MaxBytesPerSecond = 1_000, Retry = retry }, operation.Options);
    }

    [Fact]
    public void Building_without_a_target_fails()
    {
        Assert.Throws<InvalidOperationException>(() => Transfer.From(new MemorySource([])).Build());
    }

    [Fact]
    public void Invalid_options_are_rejected_when_set()
    {
        var builder = Transfer.From(new MemorySource([]));

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithMaxConnections(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithMaxRate(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferOptions { IdleTimeout = TimeSpan.Zero });
        Assert.Throws<ArgumentNullException>(() => new TransferOptions { Retry = null! });
    }

    [Fact]
    public async Task An_operation_runs_only_once()
    {
        var operation = Transfer.From(new MemorySource([1])).To(new MemoryTarget()).Build();
        await operation.RunAsync(cancellationToken: Token);

        Assert.Throws<InvalidOperationException>(() => { _ = operation.RunAsync(cancellationToken: Token); });
    }
}
