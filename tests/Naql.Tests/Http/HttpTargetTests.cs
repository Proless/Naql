using Naql.Files;
using Naql.Http;
using Naql.Streams;
using Naql.Tests.Fakes;
using System.Net;
using System.Net.Http.Headers;

namespace Naql.Tests.Http;

/// <summary>
/// HTTP uploads on the transfer engine, against an in-memory server.
/// </summary>
public sealed class HttpTargetTests : IDisposable
{
    private static readonly TransferOptions Options = new()
    {
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false },
        MinPartSize = 1_000
    };

    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private static HttpTransport Transport(FakeUploadServer server) => new(server.CreateClient());

    private static Task<TransferResult> RunAsync(ITransferSource source, HttpTarget target, TransferOptions? options = null)
    {
        return new TransferClient(options ?? Options).RunAsync(source, target, cancellationToken: Token);
    }

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        new Random(3).NextBytes(content);
        return content;
    }

    private string CreateFile(string name, byte[] content)
    {
        var path = _temp.File(name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task Put_uploads_a_file_with_its_length()
    {
        var content = Content(50_000);
        var server = new FakeUploadServer();

        var result = await RunAsync(FileSource.At(CreateFile("in.bin", content)), Transport(server).Put(FakeUploadServer.Uri), Options with { MaxConnections = 4 });

        var upload = Assert.Single(server.Uploads);
        Assert.Equal(HttpMethod.Put, upload.Method);
        Assert.Equal(content, upload.Body);
        Assert.Equal(50_000, upload.ContentLength);
        Assert.Equal("application/octet-stream", upload.ContentType);
        Assert.StartsWith("Naql.Http/", upload.UserAgent);
        Assert.Equal(50_000, result.Transferred);
        Assert.Equal(1, result.Parts);
    }

    [Fact]
    public async Task Post_sends_the_content_as_the_body_with_its_type()
    {
        var content = Content(1_000);
        var server = new FakeUploadServer();
        var source = new NamedSource(new MemorySource(content), contentType: "image/png");

        await RunAsync(source, Transport(server).Post(FakeUploadServer.Uri));

        var upload = Assert.Single(server.Uploads);
        Assert.Equal(HttpMethod.Post, upload.Method);
        Assert.Equal("image/png", upload.ContentType);
        Assert.Equal(content, upload.Body);
    }

    [Fact]
    public async Task PostForm_sends_a_multipart_form_with_the_file_name()
    {
        var content = Content(1_000);
        var server = new FakeUploadServer();

        await RunAsync(FileSource.At(CreateFile("report.pdf", content)), Transport(server).PostForm(FakeUploadServer.Uri, "document"));

        var upload = Assert.Single(server.Uploads);
        Assert.Equal("multipart/form-data", upload.ContentType);
        // The form has a length, so it isn't sent in chunks
        Assert.True(upload.ContentLength > content.Length);
        Assert.Equal("document", upload.FormField);
        Assert.Equal("report.pdf", upload.FormFileName);
        Assert.Equal("application/octet-stream", upload.FileContentType);
        Assert.Equal(content, upload.Body);
    }

    [Fact]
    public async Task Content_of_unknown_length_is_sent_without_a_length()
    {
        var content = Content(10_000);
        var server = new FakeUploadServer();

        await RunAsync(new StreamSource(new NonSeekableStream(new MemoryStream(content))), Transport(server).Put(FakeUploadServer.Uri));

        var upload = Assert.Single(server.Uploads);
        Assert.Null(upload.ContentLength);
        Assert.Equal(content, upload.Body);
    }

    [Fact]
    public async Task A_server_error_sends_the_whole_content_again()
    {
        var content = Content(10_000);
        var server = new FakeUploadServer
        {
            Respond = number => number == 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null
        };

        var result = await RunAsync(new MemorySource(content), Transport(server).Put(FakeUploadServer.Uri));

        Assert.Equal(2, server.Uploads.Count);
        Assert.All(server.Uploads, upload => Assert.Equal(content, upload.Body));
        Assert.Equal(1, result.Retries);
    }

    [Fact]
    public async Task A_post_the_server_received_completely_isnt_sent_again()
    {
        var server = new FakeUploadServer
        {
            Respond = number => number == 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null
        };

        // The server may have stored the first upload before failing, so sending it again could store it twice
        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(new MemorySource(Content(10_000)), Transport(server).Post(FakeUploadServer.Uri)));

        Assert.Equal(HttpStatusCode.BadGateway, Assert.IsType<HttpTransferException>(ex.Error).StatusCode);
        Assert.True(ex.IsTransient);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task A_post_is_sent_again_when_the_source_failed_before_the_server_had_all_of_it()
    {
        var content = Content(10_000);
        var server = new FakeUploadServer();
        var source = new MemorySource(content)
        {
            WrapContent = (index, _, stream) => index == 0 ? new FailingStream(stream, 4_000, () => new IOException("Disk read error")) : stream
        };

        var result = await RunAsync(source, Transport(server).PostForm(FakeUploadServer.Uri));

        Assert.Equal(content, Assert.Single(server.Uploads).Body);
        Assert.Equal(1, result.Retries);
    }

    [Fact]
    public void Only_a_put_can_be_written_again()
    {
        var transport = HttpTransport.Default;

        Assert.True(transport.Put(FakeUploadServer.Uri).Capabilities.RepeatableWrites);
        Assert.False(transport.Post(FakeUploadServer.Uri).Capabilities.RepeatableWrites);
        Assert.False(transport.PostForm(FakeUploadServer.Uri).Capabilities.RepeatableWrites);
    }

    [Fact]
    public async Task A_source_failure_during_the_upload_starts_it_over()
    {
        var content = Content(10_000);
        var server = new FakeUploadServer();
        var source = new MemorySource(content)
        {
            WrapContent = (index, _, stream) => index == 0 ? new FailingStream(stream, 4_000, () => new IOException("Disk read error")) : stream
        };

        var result = await RunAsync(source, Transport(server).Put(FakeUploadServer.Uri));

        // The first request never completed, so the server only recorded the second one
        Assert.Equal(content, Assert.Single(server.Uploads).Body);
        Assert.Equal(new ByteRange[] { ByteRange.All, ByteRange.All }, source.Reads.Select(read => read.Range));
        Assert.Equal(1, result.Retries);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, TransferErrorKind.AccessDenied)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, TransferErrorKind.InsufficientStorage)]
    [InlineData(HttpStatusCode.InsufficientStorage, TransferErrorKind.InsufficientStorage)]
    [InlineData(HttpStatusCode.Conflict, TransferErrorKind.Protocol)]
    public async Task Rejected_uploads_fail_without_retrying(HttpStatusCode status, TransferErrorKind kind)
    {
        var server = new FakeUploadServer { Respond = _ => new HttpResponseMessage(status) };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(new MemorySource(Content(100)), Transport(server).Put(FakeUploadServer.Uri)));

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(TransferSide.Target, ex.Side);
        Assert.False(ex.IsTransient);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task The_response_handler_sees_the_servers_answer()
    {
        var server = new FakeUploadServer();
        string? answer = null;
        HttpStatusCode? status = null;
        var target = Transport(server).Put(FakeUploadServer.Uri).WithResponseHandler(async (response, ct) =>
        {
            status = response.StatusCode;
            answer = await response.Content.ReadAsStringAsync(ct);
        });

        await RunAsync(new MemorySource(Content(100)), target);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("{\"id\":42}", answer);
    }

    [Fact]
    public async Task The_result_points_at_the_location_the_server_named()
    {
        var server = new FakeUploadServer
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.Created) { Headers = { Location = new Uri("/files/42", UriKind.Relative) } }
        };

        var result = await RunAsync(new MemorySource(Content(100)), Transport(server).Post(FakeUploadServer.Uri));

        Assert.Equal("http://test.local/files/42", result.Location);
    }

    [Fact]
    public async Task Without_a_location_the_result_points_at_the_upload_url()
    {
        var result = await RunAsync(new MemorySource(Content(100)), Transport(new FakeUploadServer()).Put(FakeUploadServer.Uri));

        Assert.Equal(FakeUploadServer.Uri.AbsoluteUri, result.Location);
    }

    [Fact]
    public async Task Sends_the_configured_headers()
    {
        AuthenticationHeaderValue? authorization = null;
        var server = new FakeUploadServer();
        var transport = new HttpTransport(server.CreateClient())
        {
            ConfigureRequest = async (request, ct) =>
            {
                await HttpTransport.BasicAuthentication("user", "pass")(request, ct);
                authorization = request.Headers.Authorization;
            }
        };

        await RunAsync(new MemorySource(Content(100)), transport.Put(FakeUploadServer.Uri));

        Assert.Equal("Basic dXNlcjpwYXNz", authorization?.ToString());
    }

    [Fact]
    public void Rejects_urls_that_arent_http()
    {
        Assert.Throws<ArgumentException>(() => HttpTarget.Put("file:///c:/upload.bin"));
    }

    /// <summary>
    /// Gives another source's content a media type.
    /// </summary>
    private sealed class NamedSource(ITransferSource inner, string contentType) : ITransferSource
    {
        public async Task<SourceInfo> GetInfoAsync(CancellationToken cancellationToken) => (await inner.GetInfoAsync(cancellationToken)) with { ContentType = contentType };

        public async Task<SourceStream> OpenReadAsync(ReadRequest request, CancellationToken cancellationToken)
        {
            var stream = await inner.OpenReadAsync(request, cancellationToken);
            return new SourceStream(stream.Content, stream.Info with { ContentType = contentType });
        }
    }
}
