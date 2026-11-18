using Naql.Files;
using Naql.Http;
using Naql.Tests.Fakes;
using System.Security.Cryptography;

namespace Naql.Tests.Transfers;

/// <summary>
/// Verifying content against a checksum before the target makes it visible.
/// </summary>
public sealed class ChecksumTests : IDisposable
{
    private static readonly TransferOptions Options = new()
    {
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false },
        MinPartSize = 1_000,
        BufferSize = 1_000
    };

    private static readonly TransferOptions Parallel = Options with { MaxConnections = 4 };

    private static readonly TargetCapabilities Verifiable = new() { ParallelParts = true, ResumableParts = true, Resumable = true, Verifiable = true };

    private readonly TempDirectory _temp = new();
    private readonly byte[] _content = Content(10_000);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        new Random(17).NextBytes(content);
        return content;
    }

    private Checksum Right => new(HashAlgorithmName.SHA256, SHA256.HashData(_content));

    private static Checksum Wrong => new(HashAlgorithmName.SHA256, new byte[32]);

    private static Task<TransferResult> RunAsync(ITransferSource source, ITransferTarget target, TransferOptions options)
    {
        return new TransferClient(options).RunAsync(source, target, cancellationToken: Token);
    }

    #region Checksum

    [Fact]
    public void A_checksum_reads_hexadecimal_and_base64()
    {
        var hash = SHA256.HashData("hello"u8);

        var fromHex = Checksum.Sha256(Convert.ToHexString(hash));
        var fromBase64 = Checksum.FromBase64(HashAlgorithmName.SHA256, Convert.ToBase64String(hash));

        Assert.Equal(fromHex, fromBase64);
        Assert.Equal(fromHex.GetHashCode(), fromBase64.GetHashCode());
        Assert.True(fromHex.Matches(hash));
        Assert.Equal($"SHA256:{Convert.ToHexString(hash).ToLowerInvariant()}", fromHex.ToString());
        Assert.NotEqual(fromHex, new Checksum(HashAlgorithmName.SHA512, SHA512.HashData("hello"u8)));
    }

    [Fact]
    public void A_checksum_rejects_invalid_values()
    {
        Assert.Throws<ArgumentException>(() => new Checksum(HashAlgorithmName.SHA256, new byte[31]));
        Assert.Throws<ArgumentException>(() => new Checksum(new HashAlgorithmName("CRC32"), new byte[4]));
        Assert.Throws<FormatException>(() => Checksum.Sha256("not hex"));
    }

    #endregion

    #region Required checksums

    [Fact]
    public async Task One_connection_is_verified_while_the_bytes_pass()
    {
        var target = new MemoryTarget();

        var result = await RunAsync(new MemorySource(_content), target, Options with { ExpectedChecksum = Right });

        Assert.Equal(Right, result.VerifiedChecksum);
        Assert.True(target.Session.Completed);
        Assert.Equal(0, target.Session.HashReads);
    }

    [Fact]
    public async Task A_mismatch_fails_and_discards_what_was_written()
    {
        var target = new MemoryTarget();

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(new MemorySource(_content), target, Options with { ExpectedChecksum = Wrong }));

        Assert.Equal(TransferErrorKind.Integrity, ex.Kind);
        Assert.Equal(TransferSide.Engine, ex.Side);
        Assert.False(ex.IsTransient);
        Assert.False(target.Session.Completed);
        Assert.True(target.Session.Aborted);
    }

    [Fact]
    public async Task Parallel_parts_are_verified_by_reading_the_target_back()
    {
        var target = new MemoryTarget { Capabilities = Verifiable };

        var result = await RunAsync(new MemorySource(_content), target, Parallel with { ExpectedChecksum = Right });

        Assert.Equal(4, result.Parts);
        Assert.Equal(Right, result.VerifiedChecksum);
        Assert.Equal(1, target.Session.HashReads);
    }

    [Fact]
    public async Task A_target_that_cant_read_back_gets_one_connection_so_the_bytes_can_be_hashed()
    {
        var target = new MemoryTarget { Capabilities = Verifiable with { Verifiable = false } };

        var result = await RunAsync(new MemorySource(_content), target, Parallel with { ExpectedChecksum = Right });

        Assert.Equal(1, result.Parts);
        Assert.Equal(Right, result.VerifiedChecksum);
        Assert.Equal(0, target.Session.HashReads);
    }

    [Fact]
    public async Task A_part_that_continues_after_a_source_failure_keeps_its_hash()
    {
        var source = new MemorySource(_content)
        {
            WrapContent = (index, _, stream) => index == 0 ? new FailingStream(stream, 4_000, () => new IOException("Reset")) : stream
        };

        var result = await RunAsync(source, new MemoryTarget(), Options with { ExpectedChecksum = Right });

        Assert.Equal(new ByteRange(4_000), source.Reads[1].Range);
        Assert.Equal(Right, result.VerifiedChecksum);
    }

    [Fact]
    public async Task A_part_that_starts_over_starts_its_hash_over()
    {
        var target = new MemoryTarget { FailFirstWriteAfter = 3_000 };

        var result = await RunAsync(new MemorySource(_content), target, Options with { ExpectedChecksum = Right });

        Assert.Equal(1, result.Retries);
        Assert.Equal(Right, result.VerifiedChecksum);
    }

    [Fact]
    public async Task A_parallel_file_download_with_the_wrong_checksum_leaves_no_file()
    {
        var path = _temp.File("out.bin");

        await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(new MemorySource(_content), FileTarget.At(path), Parallel with { ExpectedChecksum = Wrong }));

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + FileTarget.PartialExtension));
    }

    [Fact]
    public async Task A_resumed_file_download_is_verified_by_reading_the_file_back()
    {
        var path = _temp.File("out.bin");
        var checkpoint = await CancelAfterAsync(path, 3_000);

        var result = await new TransferClient(Options with { ExpectedChecksum = Right })
            .From(new MemorySource(_content)).To(FileTarget.At(path)).ResumeFrom(checkpoint).RunAsync(cancellationToken: Token);

        Assert.Equal(3_000, result.Resumed);
        Assert.Equal(Right, result.VerifiedChecksum);
        Assert.Equal(_content, File.ReadAllBytes(path));
    }

    #endregion

    #region Checksums from the source

    [Fact]
    public async Task A_checksum_from_the_source_is_verified_while_the_bytes_pass()
    {
        var target = new MemoryTarget();

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(new MemorySource(_content) { Checksum = Wrong }, target, Options));

        Assert.Equal(TransferErrorKind.Integrity, ex.Kind);
        Assert.True(target.Session.Aborted);
    }

    [Fact]
    public async Task A_checksum_from_the_source_isnt_worth_reading_the_target_back()
    {
        var target = new MemoryTarget { Capabilities = Verifiable };

        // The checksum is wrong, but checking it would take a second read of everything, which nobody asked for
        var result = await RunAsync(new MemorySource(_content) { Checksum = Wrong }, target, Parallel);

        Assert.Null(result.VerifiedChecksum);
        Assert.Equal(0, target.Session.HashReads);
        Assert.True(target.Session.Completed);
    }

    [Fact]
    public async Task The_callers_checksum_wins_over_the_sources()
    {
        var result = await RunAsync(new MemorySource(_content) { Checksum = Wrong }, new MemoryTarget(), Options with { ExpectedChecksum = Right });

        Assert.Equal(Right, result.VerifiedChecksum);
    }

    #endregion

    #region HTTP digests

    [Theory]
    [InlineData("Repr-Digest", "sha-256=:{0}:")]
    [InlineData("Repr-Digest", "unixsum=:AAAA:, sha-256=:{0}:")]
    [InlineData("Digest", "SHA-256={0}")]
    public async Task HttpSource_reads_the_checksum_from_digest_headers(string header, string format)
    {
        var server = new FakeHttpServer();
        var hash = SHA256.HashData(server.Content);
        server.Headers[header] = string.Format(format, Convert.ToBase64String(hash));

        var info = await new HttpTransport(server.CreateClient()).Get(FakeHttpServer.Uri).GetInfoAsync(Token);

        Assert.Equal(new Checksum(HashAlgorithmName.SHA256, hash), info.Checksum);
    }

    [Fact]
    public async Task HttpSource_prefers_sha512_and_ignores_malformed_digests()
    {
        var server = new FakeHttpServer();
        var sha512 = SHA512.HashData(server.Content);
        server.Headers["Repr-Digest"] = $"sha-256=:broken:, sha-512=:{Convert.ToBase64String(sha512)}:";

        var info = await new HttpTransport(server.CreateClient()).Get(FakeHttpServer.Uri).GetInfoAsync(Token);

        Assert.Equal(new Checksum(HashAlgorithmName.SHA512, sha512), info.Checksum);
    }

    [Fact]
    public async Task A_download_whose_server_digest_doesnt_match_fails()
    {
        var server = new FakeHttpServer();
        server.Headers["Repr-Digest"] = $"sha-256=:{Convert.ToBase64String(new byte[32])}:";
        var path = _temp.File("out.bin");

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(new HttpTransport(server.CreateClient()).Get(FakeHttpServer.Uri), FileTarget.At(path), Options));

        Assert.Equal(TransferErrorKind.Integrity, ex.Kind);
        Assert.False(File.Exists(path));
    }

    #endregion

    /// <summary>
    /// Downloads to <paramref name="path"/>, cancels once <paramref name="bytes"/> arrived, and returns the checkpoint.
    /// </summary>
    private async Task<TransferCheckpoint> CancelAfterAsync(string path, long bytes)
    {
        StallingStream? stalling = null;
        var source = new MemorySource(_content) { WrapContent = (_, _, stream) => stalling = new StallingStream(stream, bytes, honorCancellation: true) };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var operation = new TransferClient(Options).From(source).To(FileTarget.At(path)).Build();

        var run = operation.RunAsync(cancellationToken: cts.Token);
        while (stalling is not { Stalled.IsCompleted: true })
        {
            await Task.Delay(10, Token);
        }
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        return operation.GetCheckpoint()!;
    }
}
