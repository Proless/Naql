using Naql.Http;
using Naql.Tests.Fakes;
using System.Net;
using System.Net.Http.Headers;

namespace Naql.Tests.Http;

/// <summary>
/// Servers that answer in unusual or wrong ways: missing or wrong ranges, bodies of the wrong length, content of
/// unknown length, and versions that can or can't be used to resume.
/// </summary>
public sealed class HttpEdgeCaseTests
{
    private static readonly TransferOptions Parallel = new()
    {
        MaxConnections = 4,
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false },
        MinSplitPartSize = 10_000,
        ResponseTimeout = TimeSpan.FromSeconds(10),
        IdleTimeout = TimeSpan.FromSeconds(10)
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static HttpSource Source(FakeHttpServer server) => new HttpTransport(server.CreateClient()).Get(FakeHttpServer.Uri);

    private static Task<TransferResult> RunAsync(FakeHttpServer server, MemoryTarget target, TransferOptions? options = null)
    {
        return new TransferClient(options ?? Parallel).From(Source(server)).To(target).RunAsync(cancellationToken: Token);
    }

    /// <summary>
    /// A 206 response for <paramref name="from"/> to <paramref name="to"/> with whatever Content-Range the test gives.
    /// </summary>
    private static HttpResponseMessage Partial(FakeHttpServer server, long from, long to, ContentRangeHeaderValue? contentRange, int? bodyLength = null)
    {
        var length = bodyLength ?? (int)(to - from + 1);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(server.Content, (int)from, length) };
        response.Content.Headers.ContentRange = contentRange;
        response.Headers.ETag = server.ETag == null ? null : EntityTagHeaderValue.Parse(server.ETag);
        response.Headers.AcceptRanges.Add("bytes");
        return response;
    }

    private static (long From, long To)? Requested(HttpRequestMessage request)
    {
        return request.Headers.Range?.Ranges.Single() is { From: long from } item ? (from, item.To ?? long.MaxValue) : null;
    }

    [Fact]
    public async Task A_probe_answered_without_a_content_range_falls_back_to_one_connection()
    {
        var server = new FakeHttpServer();
        server.Intercept = request => Requested(request) is { From: 0, To: 0 } ? Partial(server, 0, 0, contentRange: null) : null;
        var target = new MemoryTarget();

        var result = await RunAsync(server, target);

        Assert.Equal(server.Content, target.Session.Data);
        Assert.Equal(SingleConnectionReason.UnknownLength, result.SingleConnectionReason);
    }

    [Fact]
    public async Task A_range_answered_without_a_content_range_fails_as_a_protocol_error()
    {
        var server = new FakeHttpServer();
        server.Intercept = request => Requested(request) is { } range ? Partial(server, range.From, range.To, contentRange: null) : null;

        var ex = await Assert.ThrowsAsync<HttpTransferException>(() => Source(server).OpenReadAsync(new ReadRequest(new ByteRange(1_000, 1_000)), Token));

        Assert.Equal(TransferErrorKind.Protocol, ex.Kind);
    }

    [Fact]
    public async Task A_range_answered_from_the_wrong_offset_fails_as_a_protocol_error()
    {
        var server = new FakeHttpServer();
        server.Intercept = request => Requested(request) is { } range
            ? Partial(server, range.From + 10, range.To + 10, new ContentRangeHeaderValue(range.From + 10, range.To + 10, server.Content.Length))
            : null;

        var ex = await Assert.ThrowsAsync<HttpTransferException>(() => Source(server).OpenReadAsync(new ReadRequest(new ByteRange(1_000, 1_000)), Token));

        Assert.Equal(TransferErrorKind.Protocol, ex.Kind);
        Assert.Contains("1010", ex.Message);
    }

    [Fact]
    public async Task A_range_answered_with_more_bytes_than_asked_is_cut_at_the_part()
    {
        var server = new FakeHttpServer();
        server.Intercept = request => Requested(request) is { } range && range.To > 0
            ? Partial(server, range.From, Math.Min(range.From + 30_000, server.Content.Length - 1),
                new ContentRangeHeaderValue(range.From, Math.Min(range.From + 30_000, server.Content.Length - 1), server.Content.Length))
            : null;
        var target = new MemoryTarget();

        var result = await RunAsync(server, target);

        Assert.Equal(server.Content, target.Session.Data);
        Assert.Equal(4, result.Parts);
    }

    [Fact]
    public async Task A_body_that_ends_before_its_range_is_retried()
    {
        var server = new FakeHttpServer();
        var cut = 0;
        server.Intercept = request => Requested(request) is { } range && range.To > 0 && Interlocked.Exchange(ref cut, 1) == 0
            ? Partial(server, range.From, range.To, new ContentRangeHeaderValue(range.From, range.To, server.Content.Length), bodyLength: 5_000)
            : null;
        var target = new MemoryTarget();

        var result = await RunAsync(server, target);

        Assert.Equal(server.Content, target.Session.Data);
        Assert.Equal(1, result.Retries);
    }

    [Fact]
    public async Task Content_of_unknown_length_is_downloaded_over_one_connection()
    {
        var server = new FakeHttpServer();
        server.Intercept = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(server.Content) };
            response.Headers.ETag = EntityTagHeaderValue.Parse(server.ETag!);
            return response;
        };
        var target = new MemoryTarget();

        var result = await RunAsync(server, target);

        Assert.Equal(server.Content, target.Session.Data);
        Assert.Null(result.Source.Length);
        Assert.Equal(SingleConnectionReason.UnknownLength, result.SingleConnectionReason);
    }

    [Fact]
    public async Task An_unsatisfiable_first_byte_of_content_that_isnt_empty_is_a_protocol_error()
    {
        var server = new FakeHttpServer();
        server.Intercept = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(server.Content.Length);
            return response;
        };

        var ex = await Assert.ThrowsAsync<HttpTransferException>(() => Source(server).GetInfoAsync(Token));

        Assert.Equal(TransferErrorKind.Protocol, ex.Kind);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, ex.StatusCode);
    }

    [Fact]
    public async Task A_weak_etag_isnt_a_version_so_the_content_isnt_split()
    {
        var server = new FakeHttpServer { ETag = "W/\"v1\"" };
        var target = new MemoryTarget();

        var result = await RunAsync(server, target);

        Assert.Equal(server.Content, target.Session.Data);
        Assert.Null(result.Source.Version);
        Assert.Equal(SingleConnectionReason.NoVersion, result.SingleConnectionReason);
    }

    [Fact]
    public async Task Last_modified_alone_is_a_version_that_if_range_checks()
    {
        var modified = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var server = new FakeHttpServer { ETag = null, LastModified = modified };
        var ifRanges = new List<RangeConditionHeaderValue?>();
        server.Intercept = request =>
        {
            if (Requested(request) is { To: > 0 })
            {
                lock (ifRanges)
                {
                    ifRanges.Add(request.Headers.IfRange);
                }
            }
            return null;
        };
        var target = new MemoryTarget();

        var result = await RunAsync(server, target);

        Assert.Equal(server.Content, target.Session.Data);
        Assert.Equal(4, result.Parts);
        Assert.Equal(modified.ToString("R"), result.Source.Version);
        Assert.All(ifRanges, ifRange => Assert.Equal(modified, ifRange?.Date));
    }

    [Fact]
    public async Task Content_whose_last_modified_changed_starts_over()
    {
        var server = new FakeHttpServer { ETag = null, LastModified = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero) };
        var probed = 0;
        server.Intercept = request =>
        {
            // The file changes right after the server described it
            if (Requested(request) is { From: 0, To: 0 } && Interlocked.Increment(ref probed) == 1)
            {
                var response = Partial(server, 0, 0, new ContentRangeHeaderValue(0, 0, server.Content.Length));
                response.Headers.ETag = null;
                response.Content.Headers.LastModified = server.LastModified;
                server.LastModified = server.LastModified.Value.AddMinutes(5);
                return response;
            }
            return null;
        };
        var target = new MemoryTarget();

        var result = await RunAsync(server, target);

        Assert.Equal(server.Content, target.Sessions[^1].Data);
        Assert.Equal(server.LastModified.Value.ToString("R"), result.Source.Version);
    }

    /// <summary>
    /// Content sent without a Content-Length, like a chunked response.
    /// </summary>
    private sealed class UnknownLengthContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(content, 0, content.Length);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new NonSeekableStream(new MemoryStream(content)));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
