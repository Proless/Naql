using Naql.Engine;
using Naql.Tests.Fakes;

namespace Naql.Tests.Transfers;

/// <summary>
/// Splitting running parts when a connection frees up, so a slow part doesn't hold up the end of a transfer.
/// </summary>
public sealed class SplittingTests
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

    private static readonly TargetCapabilities Splittable = new() { ParallelParts = true, SplittableParts = true, ResumableParts = true, Resumable = true };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        new Random(42).NextBytes(content);
        return content;
    }

    // Reads from 20,000 on, the second of two planned parts, are slow; parts split off later read fast
    private static MemorySource SlowSecondHalf(byte[] content) => new(content)
    {
        WrapContent = (_, request, stream) => request.Range.Start == 20_000 ? new SlowStream(stream, TimeSpan.FromMilliseconds(5)) : stream
    };

    // The first read is slow; parts split off later read fast
    private static MemorySource SlowFirstRead(byte[] content) => new(content)
    {
        WrapContent = (index, _, stream) => index == 0 ? new SlowStream(stream, TimeSpan.FromMilliseconds(5)) : stream
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
    public async Task A_connection_that_frees_up_takes_the_end_of_a_slow_part()
    {
        var content = Content(40_000);
        var source = SlowSecondHalf(content);
        var target = new MemoryTarget { Capabilities = Splittable };

        var result = await Transfer.From(source).To(target).WithOptions(Options with { MaxConnections = 2 }).RunAsync(cancellationToken: Token);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(40_000, result.Transferred);
        Assert.True(result.Parts > 2, $"{result.Parts} parts");
        Assert.Contains(source.Reads, read => read.Range.Start > 20_000);
        Assert.All(source.Reads, read => Assert.Equal("v1", read.IfVersion));
    }

    [Fact]
    public async Task A_target_that_cant_take_split_parts_keeps_the_planned_ones()
    {
        var content = Content(40_000);
        var target = new MemoryTarget { Capabilities = Splittable with { SplittableParts = false } };

        var result = await Transfer.From(SlowSecondHalf(content)).To(target).WithOptions(Options with { MaxConnections = 2 }).RunAsync(cancellationToken: Token);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(2, result.Parts);
    }

    [Fact]
    public async Task Without_adaptive_splitting_the_planned_parts_stay()
    {
        var content = Content(40_000);
        var target = new MemoryTarget { Capabilities = Splittable };
        var options = Options with { MaxConnections = 2, AdaptiveSplitting = false };

        var result = await Transfer.From(SlowSecondHalf(content)).To(target).WithOptions(options).RunAsync(cancellationToken: Token);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(2, result.Parts);
    }

    [Fact]
    public async Task Without_adaptive_splitting_more_connections_dont_split_a_running_transfer()
    {
        var content = Content(40_000);
        var target = new MemoryTarget { Capabilities = Splittable };
        var operation = Transfer.From(SlowFirstRead(content)).To(target).WithOptions(Options with { AdaptiveSplitting = false }).Build();
        long transferred = 0;

        var run = operation.RunAsync(new SynchronousProgress(p => Interlocked.Exchange(ref transferred, p.Transferred)), Token);
        await WaitUntilAsync(() => Interlocked.Read(ref transferred) > 0);
        operation.SetMaxConnections(4);
        var result = await run;

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Parts);
    }

    [Fact]
    public void Adaptive_splitting_is_on_by_default()
    {
        Assert.True(new TransferOptions().AdaptiveSplitting);
    }

    [Fact]
    public async Task A_target_with_a_part_limit_gets_no_more_parts()
    {
        var content = Content(40_000);
        var target = new MemoryTarget { Capabilities = Splittable with { MaxParts = 2 } };

        var result = await Transfer.From(SlowSecondHalf(content)).To(target).WithOptions(Options with { MaxConnections = 2 }).RunAsync(cancellationToken: Token);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(2, result.Parts);
    }

    [Fact]
    public async Task More_connections_split_a_transfer_that_started_with_one()
    {
        var content = Content(40_000);
        var target = new MemoryTarget { Capabilities = Splittable };
        var operation = Transfer.From(SlowFirstRead(content)).To(target).WithOptions(Options).Build();
        long transferred = 0;
        var checkpointErrors = new List<string>();
        var progress = new SynchronousProgress(p =>
        {
            Interlocked.Exchange(ref transferred, p.Transferred);
            // Checkpoints taken while parts split must still cover the content in order
            if (operation.GetCheckpoint() is { } checkpoint)
            {
                long end = 0;
                foreach (var part in checkpoint.Parts)
                {
                    if (part.Range.Start != end || part.Completed > part.Range.Length)
                    {
                        lock (checkpointErrors)
                        {
                            checkpointErrors.Add($"{part.Range} after {end}");
                        }
                    }
                    end = part.Range.Start + part.Range.Length!.Value;
                }
                if (end != content.Length)
                {
                    lock (checkpointErrors)
                    {
                        checkpointErrors.Add($"The parts end at {end}");
                    }
                }
            }
        });

        var run = operation.RunAsync(progress, Token);
        await WaitUntilAsync(() => Interlocked.Read(ref transferred) > 0);
        operation.SetMaxConnections(4);
        var result = await run;

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(40_000, result.Transferred);
        Assert.True(result.Parts > 1, $"{result.Parts} parts");
        Assert.True(target.Session.MaxActiveWrites > 1);
        Assert.Empty(checkpointErrors);
    }

    [Fact]
    public async Task A_part_whose_bytes_are_hashed_as_they_pass_isnt_split()
    {
        var content = Content(40_000);
        var checksum = Checksum.Sha256(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)));
        var target = new MemoryTarget { Capabilities = Splittable };
        var operation = Transfer.From(SlowFirstRead(content)).To(target).WithOptions(Options).WithExpectedChecksum(checksum).Build();
        long transferred = 0;

        var run = operation.RunAsync(new SynchronousProgress(p => Interlocked.Exchange(ref transferred, p.Transferred)), Token);
        await WaitUntilAsync(() => Interlocked.Read(ref transferred) > 0);
        operation.SetMaxConnections(4);
        var result = await run;

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Parts);
        Assert.Equal(checksum, result.VerifiedChecksum);
    }

    [Fact]
    public async Task A_source_that_allows_one_connection_isnt_split_when_more_are_allowed_later()
    {
        var content = Content(40_000);
        var source = new MemorySource(content)
        {
            MaxConnections = 1,
            WrapContent = (_, _, stream) => new SlowStream(stream, TimeSpan.FromMilliseconds(2))
        };
        var target = new MemoryTarget { Capabilities = Splittable };
        var operation = Transfer.From(source).To(target).WithOptions(Options).Build();
        long transferred = 0;

        var run = operation.RunAsync(new SynchronousProgress(p => Interlocked.Exchange(ref transferred, p.Transferred)), Token);
        await WaitUntilAsync(() => Interlocked.Read(ref transferred) > 0);
        operation.SetMaxConnections(4);
        var result = await run;

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Parts);
        Assert.Single(source.Reads);
    }

    [Fact]
    public async Task A_source_limit_learned_from_the_first_read_holds_for_connections_raised_before_it()
    {
        var content = Content(40_000);
        TransferOperation? operation = null;
        var source = new MemorySource(content)
        {
            MaxConnections = 1,
            // Raised once the transfer runs, but before the first read describes the source
            OnOpen = (index, _, _) =>
            {
                if (index == 0)
                {
                    operation!.SetMaxConnections(4);
                }
                return Task.CompletedTask;
            },
            WrapContent = (_, _, stream) => new SlowStream(stream, TimeSpan.FromMilliseconds(2))
        };
        var target = new MemoryTarget { Capabilities = Splittable };
        operation = Transfer.From(source).To(target).WithOptions(Options).Build();

        var result = await operation.RunAsync(cancellationToken: Token);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Parts);
        Assert.Single(source.Reads);
    }

    [Fact]
    public async Task Split_parts_of_a_file_download_put_every_byte_in_place()
    {
        using var temp = new TempDirectory();
        var content = Content(200_000);
        var path = temp.File("out.bin");

        var result = await Transfer.From(SlowFirstRead(content)).To(Naql.Files.FileTarget.At(path))
            .WithOptions(Options with { MaxConnections = 2, MinSplitPartSize = 10_000 }).RunAsync(cancellationToken: Token);

        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.True(result.Parts > 2, $"{result.Parts} parts");
    }

    [Theory]
    [InlineData(0, 0, 10_000, 5_000L)] // halfway through what's left
    [InlineData(0, 4_000, 10_000, 7_000L)] // halfway after the bytes already taken
    [InlineData(0, 8_500, 10_000, null)] // less than twice the smallest split left
    public void A_part_splits_halfway_through_the_rest(long start, long from, long end, long? expected)
    {
        Assert.Equal(expected, PartPlanner.SplitAt(start, from, end, new TargetCapabilities { ParallelParts = true }, Options));
    }

    [Fact]
    public void A_split_starts_the_new_part_at_a_multiple_the_target_needs_and_keeps_its_minimum()
    {
        var aligned = new TargetCapabilities { ParallelParts = true, PartSizeMultiple = 4_096 };
        Assert.Equal(8_192, PartPlanner.SplitAt(0, 1_000, 10_000, aligned, Options));
        // Aligning leaves the new part too small
        Assert.Null(PartPlanner.SplitAt(0, 1_000, 10_000, aligned, Options with { MinSplitPartSize = 2_000 }));

        var minimum = new TargetCapabilities { ParallelParts = true, MinPartSize = 6_000 };
        Assert.Null(PartPlanner.SplitAt(0, 0, 10_000, minimum, Options));
        Assert.Equal(7_000, PartPlanner.SplitAt(0, 0, 14_000, minimum, Options));
    }

    private sealed class SynchronousProgress(Action<TransferProgress> handler) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => handler(value);
    }
}
