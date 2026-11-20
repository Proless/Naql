using Naql.Http;
using Naql.Tests.Fakes;
using System.Net;

namespace Naql.Tests.Http;

/// <summary>
/// Uploads through upload sessions, with a target built on <see cref="ChunkedUploadTarget"/> the way a package for a
/// real service would build it.
/// </summary>
public sealed class ChunkedUploadTargetTests
{
    private static readonly TransferOptions Options = new()
    {
        BufferSize = 1_000,
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false }
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        new Random(8).NextBytes(content);
        return content;
    }

    private static TusLikeTarget Target(FakeChunkedService service, int chunkSize = 3_000) => new(new HttpTransport(service.CreateClient()), chunkSize);

    private static Task<TransferResult> RunAsync(ITransferSource source, ITransferTarget target, TransferOptions? options = null, TransferCheckpoint? checkpoint = null)
    {
        var builder = new TransferClient(options ?? Options).From(source).To(target);
        return (checkpoint == null ? builder : builder.ResumeFrom(checkpoint)).RunAsync(cancellationToken: Token);
    }

    [Fact]
    public async Task The_content_goes_up_in_chunks_and_the_last_one_finishes_it()
    {
        var content = Content(10_000);
        var service = new FakeChunkedService();

        var result = await RunAsync(new MemorySource(content), Target(service));

        var upload = Assert.Single(service.Uploads.Values);
        Assert.Equal(content, upload.Data.ToArray());
        Assert.True(upload.Finished);
        Assert.Equal([0L, 3_000, 6_000, 9_000], service.PatchOffsets);
        Assert.Equal("http://test.local/files/1", result.Location);
        Assert.Equal("1", result.TargetMetadata["id"]);
    }

    [Fact]
    public async Task A_chunk_that_failed_is_sent_again_and_the_chunks_before_it_are_not()
    {
        var content = Content(10_000);
        var service = new FakeChunkedService { OnPatch = patch => patch == 2 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null };
        var source = new MemorySource(content);

        var result = await RunAsync(source, Target(service));

        Assert.Equal(content, Assert.Single(service.Uploads.Values).Data.ToArray());
        Assert.Equal([0L, 3_000, 6_000, 6_000, 9_000], service.PatchOffsets);
        Assert.Equal(1, result.Retries);
        Assert.Equal(new ByteRange(6_000), source.Reads[1].Range);
    }

    [Fact]
    public async Task What_the_server_didnt_take_of_a_chunk_goes_again_with_the_next_one()
    {
        var content = Content(10_000);
        var service = new FakeChunkedService { TakeAtMost = 2_500 };

        var result = await RunAsync(new MemorySource(content), Target(service));

        Assert.Equal(content, Assert.Single(service.Uploads.Values).Data.ToArray());
        Assert.Equal([0L, 2_500, 5_000, 7_500], service.PatchOffsets);
        Assert.Equal(0, result.Retries);
    }

    [Fact]
    public async Task Content_of_unknown_length_tells_the_service_its_length_with_the_last_chunk()
    {
        var content = Content(7_000);
        var service = new FakeChunkedService();

        await RunAsync(new MemorySource(content) { ReportsLength = false }, Target(service));

        var upload = Assert.Single(service.Uploads.Values);
        Assert.Equal(content, upload.Data.ToArray());
        Assert.Equal(7_000, upload.Length);
        Assert.True(upload.Finished);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3_000)]
    [InlineData(6_000)]
    public async Task Content_that_ends_on_a_chunk_boundary_finishes_with_its_last_chunk(int length)
    {
        var content = Content(length);
        var service = new FakeChunkedService();

        await RunAsync(new MemorySource(content), Target(service));

        var upload = Assert.Single(service.Uploads.Values);
        Assert.Equal(content, upload.Data.ToArray());
        Assert.True(upload.Finished);
        Assert.Equal(Math.Max(1, length / 3_000), service.Patches);
    }

    [Fact]
    public async Task A_later_run_continues_the_session_from_what_the_server_has()
    {
        var content = Content(10_000);
        var service = new FakeChunkedService { OnPatch = patch => patch == 2 ? throw new HttpRequestException("The connection was reset") : null };
        var operation = new TransferClient(Options with { Retry = RetryPolicy.None }).From(new MemorySource(content)).To(Target(service)).Build();
        await Assert.ThrowsAsync<TransferFailedException>(() => operation.RunAsync(cancellationToken: Token));
        var checkpoint = operation.GetCheckpoint()!;
        Assert.Equal(6_000, checkpoint.Parts[0].Completed);

        // A new process, a new target, the same session
        service.OnPatch = null;
        var target = Target(service);
        var source = new MemorySource(content);
        var result = await RunAsync(source, target, checkpoint: checkpoint);

        Assert.Equal(content, Assert.Single(service.Uploads.Values).Data.ToArray());
        Assert.Equal(6_000, result.Resumed);
        Assert.Equal(1, target.Queries);
        Assert.Equal(new ByteRange(6_000, 4_000), Assert.Single(source.Reads).Range);
    }

    [Fact]
    public async Task A_session_the_service_forgot_starts_the_upload_over()
    {
        var content = Content(10_000);
        var service = new FakeChunkedService { OnPatch = patch => patch == 2 ? throw new HttpRequestException("The connection was reset") : null };
        var operation = new TransferClient(Options with { Retry = RetryPolicy.None }).From(new MemorySource(content)).To(Target(service)).Build();
        await Assert.ThrowsAsync<TransferFailedException>(() => operation.RunAsync(cancellationToken: Token));
        var checkpoint = operation.GetCheckpoint()!;
        service.Forget(checkpoint.ResumeToken!);
        service.OnPatch = null;

        var result = await RunAsync(new MemorySource(content), Target(service), checkpoint: checkpoint);

        Assert.Equal(0, result.Resumed);
        Assert.Equal(content, service.Uploads["http://test.local/uploads/2"].Data.ToArray());
    }

    [Fact]
    public async Task A_transfer_that_discards_what_it_uploaded_cancels_the_session()
    {
        var service = new FakeChunkedService { OnPatch = patch => patch == 1 ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null };
        var options = Options with { DiscardPartialOnFailure = true };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(new MemorySource(Content(10_000)), Target(service), options));

        Assert.Equal(TransferErrorKind.AccessDenied, ex.Kind);
        Assert.True(Assert.Single(service.Uploads.Values).Deleted);
    }
}
