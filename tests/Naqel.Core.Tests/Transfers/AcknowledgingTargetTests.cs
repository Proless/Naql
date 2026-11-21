using System.Security.Cryptography;
using Naqel.Core.Tests.Fakes;

namespace Naqel.Core.Tests.Transfers;

/// <summary>
/// Targets that keep only what their server acknowledged, such as chunked upload sessions: parts continue from the
/// last acknowledged byte, and checkpoints count only acknowledged bytes.
/// </summary>
public sealed class AcknowledgingTargetTests
{
    private static readonly TransferOptions Options = new()
    {
        MinSplitPartSize = 1_000,
        BufferSize = 700,
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false }
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        new Random(21).NextBytes(content);
        return content;
    }

    [Fact]
    public async Task A_chunk_the_server_didnt_acknowledge_is_sent_again_and_nothing_before_it()
    {
        var content = Content(10_000);
        // The third chunk fails after it was read
        var server = new ChunkedUploadServer { OnSend = send => send == 2 ? throw new IOException("The connection was reset") : Task.CompletedTask };
        var source = new MemorySource(content);

        var result = await new TransferClient(Options).From(source).To(server.Target(chunkSize: 2_000)).RunAsync(cancellationToken: Token);

        Assert.Equal(content, server.Get("upload-1").Data);
        Assert.Equal(1, result.Retries);
        // The target failed on its own, yet the part continued from the two acknowledged chunks
        Assert.Equal(new ByteRange(4_000), source.Reads[1].Range);
        Assert.Equal(content.Length, result.Transferred);
    }

    [Fact]
    public async Task A_source_that_breaks_mid_chunk_continues_from_the_last_acknowledged_chunk()
    {
        var content = Content(10_000);
        var server = new ChunkedUploadServer();
        var source = new MemorySource(content)
        {
            WrapContent = (read, _, stream) => read == 0 ? new FailingStream(stream, 5_500, () => new IOException("The connection was reset")) : stream
        };

        await new TransferClient(Options).From(source).To(server.Target(chunkSize: 2_000)).RunAsync(cancellationToken: Token);

        Assert.Equal(content, server.Get("upload-1").Data);
        // 5 500 bytes were read, but only 4 000 acknowledged
        Assert.Equal(new ByteRange(4_000), source.Reads[1].Range);
    }

    [Fact]
    public async Task A_checkpoint_counts_only_acknowledged_bytes()
    {
        var content = Content(10_000);
        var server = new ChunkedUploadServer();
        var stalls = new List<StallingStream>();
        var source = new MemorySource(content)
        {
            WrapContent = (_, _, stream) =>
            {
                var stalling = new StallingStream(stream, 5_500, honorCancellation: true);
                lock (stalls)
                {
                    stalls.Add(stalling);
                }
                return stalling;
            }
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var operation = new TransferClient(Options).From(source).To(server.Target(chunkSize: 2_000)).Build();
        var run = operation.RunAsync(cancellationToken: cts.Token);
        while (stalls.Count == 0 || !stalls[0].Stalled.IsCompleted)
        {
            await Task.Delay(10, Token);
        }

        var checkpoint = operation.GetCheckpoint()!;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(4_000, Assert.Single(checkpoint.Parts).Completed);
        Assert.Equal(4_000, operation.GetCheckpoint()!.Parts[0].Completed);
    }

    [Theory]
    [InlineData(6_000)]
    [InlineData(2_000)]
    public async Task Resuming_continues_from_what_the_server_has_not_from_the_checkpoint(long serverHas)
    {
        var content = Content(10_000);
        var server = new ChunkedUploadServer();
        // An upload that got further, or that the server lost part of, after the checkpoint was saved at 4 000
        var target = server.Target(chunkSize: 2_000);
        var first = await target.OpenAsync(new TargetOpenRequest(new SourceInfo { Length = content.Length }), Token);
        await first.WritePartAsync(new ByteRange(0, 6_000), new MemoryStream(content, 0, 6_000), Token);
        server.Forget("upload-1", serverHas);
        var checkpoint = new TransferCheckpoint(content.Length, TransferCheckpoint.Fingerprint("memory://content"), "v1", "upload-1",
            [new CheckpointPart(new ByteRange(0, content.Length), 4_000)]);
        var source = new MemorySource(content);

        var result = await new TransferClient(Options).From(source).To(target).ResumeFrom(checkpoint).RunAsync(cancellationToken: Token);

        Assert.Equal(content, server.Get("upload-1").Data);
        Assert.Equal(serverHas, result.Resumed);
        Assert.Equal(new ByteRange(serverHas, content.Length - serverHas), Assert.Single(source.Reads).Range);
    }

    [Fact]
    public async Task A_part_hashed_while_it_streams_starts_over_when_it_loses_bytes()
    {
        var content = Content(10_000);
        var server = new ChunkedUploadServer { OnSend = send => send == 2 ? throw new IOException("The connection was reset") : Task.CompletedTask };
        var source = new MemorySource(content);
        // The target can't read back, so the checksum is hashed as the bytes pass, which only works from the first byte
        var options = Options with { ExpectedChecksum = new Checksum(HashAlgorithmName.SHA256, SHA256.HashData(content)) };

        var result = await new TransferClient(options).From(source).To(server.Target(chunkSize: 2_000, verifiable: false)).RunAsync(cancellationToken: Token);

        Assert.Equal(content, server.Get("upload-2").Data);
        Assert.Equal(new ByteRange(0), source.Reads[1].Range);
        Assert.NotNull(result.VerifiedChecksum);
    }

    [Fact]
    public async Task Needing_a_new_session_twice_fails_with_the_error_that_caused_it()
    {
        var content = Content(10_000);
        // Every session loses its third chunk, and the hash can't take bytes back, so each needs a new session
        var server = new ChunkedUploadServer { OnSend = send => send % 3 == 2 ? throw new IOException("The connection was reset") : Task.CompletedTask };
        var options = Options with { ExpectedChecksum = new Checksum(HashAlgorithmName.SHA256, SHA256.HashData(content)) };
        var target = server.Target(chunkSize: 2_000, verifiable: false);

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => new TransferClient(options).From(new MemorySource(content)).To(target).RunAsync(cancellationToken: Token));

        Assert.Equal(TransferErrorKind.Network, ex.Kind);
        Assert.Equal("The connection was reset", ex.Error.Message);
        Assert.IsNotType<Naqel.Engine.StartOverException>(ex.Error);
        Assert.All(ex.PartFailures, part => Assert.IsNotType<Naqel.Engine.StartOverException>(part.Error));
        Assert.Equal(2, target.Sessions.Count);
        Assert.DoesNotContain(target.Sessions, session => server.Get(session.ResumeToken!).Completed);
    }

    [Fact]
    public async Task The_result_has_the_uploads_location_and_metadata()
    {
        var server = new ChunkedUploadServer();

        var result = await new TransferClient(Options).From(new MemorySource(Content(3_000))).To(server.Target(chunkSize: 2_000)).RunAsync(cancellationToken: Token);

        Assert.Equal("chunked://upload-1", result.Location);
        Assert.Equal("upload-1", result.TargetMetadata["upload"]);
        Assert.True(server.Get("upload-1").Completed);
    }
}
