using Naql.Streams;
using Naql.Tests.Fakes;

namespace Naql.Tests.Streams;

/// <summary>
/// Stream sources and targets.
/// </summary>
public sealed class StreamEndpointTests
{
    private static readonly TransferOptions Options = new()
    {
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false },
        MinPartSize = 1_000
    };

    private static readonly TransferOptions Parallel = Options with { MaxConnections = 4 };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        new Random(7).NextBytes(content);
        return content;
    }

    private static Task<TransferResult> RunAsync(ITransferSource source, ITransferTarget target, TransferOptions? options = null)
    {
        return new TransferClient(options ?? Options).RunAsync(source, target, cancellationToken: Token);
    }


    [Fact]
    public async Task A_seekable_stream_source_is_read_from_its_position_in_one_part()
    {
        var content = Content(10_000);
        var stream = new MemoryStream([9, 9, 9, .. content]) { Position = 3 };
        var target = new MemoryTarget();

        var result = await RunAsync(new StreamSource(stream, "data.bin"), target, Parallel);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Parts);
        Assert.Equal("data.bin", result.Source.Name);
    }

    [Fact]
    public async Task A_seekable_stream_source_supports_parallel_parts_when_a_checksum_guards_them()
    {
        var content = Content(10_000);
        var stream = new MemoryStream([9, 9, 9, .. content]) { Position = 3 };
        var target = new MemoryTarget { Capabilities = new() { ParallelParts = true, ResumableParts = true, Resumable = true, Verifiable = true } };
        var checksum = new Checksum(System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.SHA256.HashData(content));

        var result = await RunAsync(new StreamSource(stream, "data.bin"), target, Parallel with { ExpectedChecksum = checksum });

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(4, result.Parts);
    }

    [Fact]
    public async Task A_stream_that_cant_seek_is_read_once_and_never_retried()
    {
        var content = Content(10_000);
        var stream = new NonSeekableStream(new FailingStream(new MemoryStream(content), 2_000, () => new IOException("Reset")));
        var source = new StreamSource(stream);

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, new MemoryTarget(), Parallel));

        Assert.Equal(TransferErrorKind.Network, ex.Kind);
        Assert.False(source.Info.IsRepeatable);
        Assert.Null(source.Info.Length);
    }

    [Fact]
    public async Task A_stream_that_cant_seek_is_copied_completely()
    {
        var content = Content(10_000);
        var target = new MemoryTarget();

        await RunAsync(new StreamSource(new NonSeekableStream(new MemoryStream(content))), target, Parallel);

        Assert.Equal(content, target.Session.Data);
    }

    [Fact]
    public async Task A_seekable_stream_target_takes_parallel_parts_from_its_position()
    {
        var content = Content(10_000);
        var output = new MemoryStream();
        output.Write([7, 7]);

        await RunAsync(new MemorySource(content), new StreamTarget(output), Parallel);

        Assert.Equal([7, 7, .. content], output.ToArray());
    }

    [Fact]
    public async Task A_stream_target_that_cant_seek_continues_a_part_after_a_source_failure()
    {
        var content = Content(10_000);
        var output = new MemoryStream();
        var source = new MemorySource(content) { WrapContent = (index, _, stream) => index == 0 ? new FailingStream(stream, 3_000, () => new IOException("Reset")) : stream };

        await RunAsync(source, new StreamTarget(new NonSeekableStream(output)));

        Assert.Equal(content, output.ToArray());
    }

    [Fact]
    public async Task A_stream_target_that_cant_seek_cant_write_a_part_again()
    {
        var output = new MemoryStream();
        var source = new MemorySource(Content(10_000))
        {
            SupportsRanges = false,
            WrapContent = (index, _, stream) => index == 0 ? new FailingStream(stream, 3_000, () => new IOException("Reset")) : stream
        };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, new StreamTarget(new NonSeekableStream(output))));

        Assert.Equal(TransferErrorKind.Unsupported, ex.Kind);
    }

}
