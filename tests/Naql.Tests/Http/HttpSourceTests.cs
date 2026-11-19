using Naql.Files;
using Naql.Http;
using Naql.Tests.Fakes;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Naql.Tests.Http;

/// <summary>
/// HTTP downloads on the transfer engine, against an in-memory server.
/// </summary>
public sealed class HttpSourceTests : IDisposable
{
    private static readonly TransferOptions Options = new()
    {
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false },
        MinSplitPartSize = 10_000,
        ResponseTimeout = TimeSpan.FromSeconds(10),
        IdleTimeout = TimeSpan.FromSeconds(10)
    };

    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private static HttpSource Source(FakeHttpServer server, Func<HttpRequestMessage, CancellationToken, Task>? configure = null)
    {
        return new HttpTransport(server.CreateClient()) { ConfigureRequest = configure }.Get(FakeHttpServer.Uri);
    }

    private static Task<TransferResult> RunAsync(HttpSource source, ITransferTarget target, TransferOptions? options = null, CancellationToken? cancellationToken = null)
    {
        return new TransferClient(options ?? Options).From(source).To(target).RunAsync(cancellationToken: cancellationToken ?? Token);
    }

    private static long? RangeStart(RangeHeaderValue? range) => range?.Ranges.Single().From;

    /// <summary>
    /// A full response whose body stalls after <paramref name="stallAfter"/> bytes.
    /// </summary>
    private static HttpResponseMessage StallingResponse(FakeHttpServer server, int stallAfter, out StallingStream body)
    {
        body = new StallingStream(new MemoryStream(server.Content), stallAfter, honorCancellation: true);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        response.Content.Headers.ContentLength = server.Content.Length;
        response.Headers.AcceptRanges.Add("bytes");
        response.Headers.ETag = EntityTagHeaderValue.Parse(server.ETag);
        return response;
    }

    #region Downloads

    [Fact]
    public async Task One_connection_downloads_with_a_single_request()
    {
        var server = new FakeHttpServer();
        var path = _temp.File("out.bin");

        var result = await RunAsync(Source(server), FileTarget.At(path));

        Assert.Equal(server.Content, File.ReadAllBytes(path));
        Assert.Equal(1, server.RequestCount);
        Assert.Null(Assert.Single(server.RequestedRanges));
        Assert.False(File.Exists(path + FileTarget.PartialExtension));
        Assert.Equal("\"v1\"", result.Source.Version);
        Assert.True(result.Source.SupportsRanges);
    }

    [Fact]
    public async Task Parallel_parts_write_one_identical_file()
    {
        var server = new FakeHttpServer();
        var path = _temp.File("out.bin");

        var result = await RunAsync(Source(server), FileTarget.At(path), Options with { MaxConnections = 4 });

        Assert.Equal(server.Content, File.ReadAllBytes(path));
        Assert.Equal(4, result.Parts);
        // The first request only asks for one byte, to learn the length and whether ranges work
        Assert.Equal(5, server.RequestCount);
        Assert.Equal(0, server.RequestedRanges[0]!.Ranges.Single().To);
        Assert.Equal([0L, 25_000L, 50_000L, 75_000L], server.RequestedRanges.Skip(1).Select(RangeStart).OrderBy(start => start));
    }

    [Fact]
    public async Task Uses_one_connection_when_the_server_ignores_ranges()
    {
        var server = new FakeHttpServer { SupportsRanges = false };
        var path = _temp.File("out.bin");

        var result = await RunAsync(Source(server), FileTarget.At(path), Options with { MaxConnections = 4 });

        Assert.Equal(server.Content, File.ReadAllBytes(path));
        Assert.Equal(1, result.Parts);
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task Downloads_an_empty_file()
    {
        var server = new FakeHttpServer(length: 0);
        var path = _temp.File("empty.bin");

        var result = await RunAsync(Source(server), FileTarget.At(path), Options with { MaxConnections = 4 });

        Assert.Empty(File.ReadAllBytes(path));
        Assert.Equal(0, result.Transferred);
    }

    [Theory]
    [InlineData("report v2.pdf", "report v2.pdf")]
    [InlineData("../escape.bin", "escape.bin")]
    [InlineData("..\\escape.bin", "escape.bin")]
    [InlineData("sub/dir/name.txt", "name.txt")]
    public async Task Names_the_file_from_content_disposition_inside_the_directory(string sentName, string expectedName)
    {
        var server = new FakeHttpServer { FileName = sentName };
        var directory = _temp.File("output");

        await RunAsync(Source(server), FileTarget.InDirectory(directory));

        Assert.Equal(server.Content, File.ReadAllBytes(Path.Combine(directory, expectedName)));
        Assert.False(File.Exists(_temp.File(expectedName)));
    }

    [Fact]
    public async Task Names_the_file_from_the_url_without_content_disposition()
    {
        var server = new FakeHttpServer();
        var target = FileTarget.InDirectory(_temp.Path);

        var result = await RunAsync(Source(server), target);

        Assert.Equal(_temp.File("file.bin"), result.Location);
        Assert.Equal(server.Content, File.ReadAllBytes(result.Location!));
    }

    [Fact]
    public async Task Sends_the_user_agent_and_the_configured_headers()
    {
        string? userAgent = null;
        AuthenticationHeaderValue? authorization = null;
        var server = new FakeHttpServer();
        server.Intercept = request =>
        {
            userAgent = request.Headers.UserAgent.ToString();
            authorization = request.Headers.Authorization;
            return null;
        };

        await RunAsync(Source(server, HttpTransport.BasicAuthentication("user", "pass")), new MemoryTarget());

        Assert.StartsWith("Naql.Http/", userAgent);
        Assert.Equal("Basic dXNlcjpwYXNz", authorization?.ToString());
    }

    [Fact]
    public void Rejects_urls_that_arent_http()
    {
        Assert.Throws<ArgumentException>(() => HttpSource.Get("ftp://example.com/file.bin"));
    }

    #endregion

    #region Describing and reading directly

    [Fact]
    public async Task Describes_the_content_with_a_request_for_one_byte()
    {
        var server = new FakeHttpServer();

        var info = await Source(server).GetInfoAsync(Token);

        Assert.Equal(server.Content.Length, info.Length);
        Assert.True(info.SupportsRanges);
        Assert.Equal("\"v1\"", info.Version);
        Assert.Equal("file.bin", info.Name);
        Assert.Equal(FakeHttpServer.Uri.AbsoluteUri, info.Identity);
        var probe = Assert.Single(server.RequestedRanges)!.Ranges.Single();
        Assert.Equal(0, probe.From);
        Assert.Equal(0, probe.To);
    }

    [Fact]
    public async Task Describes_a_server_without_ranges()
    {
        var server = new FakeHttpServer { SupportsRanges = false };

        var info = await Source(server).GetInfoAsync(Token);

        Assert.Equal(server.Content.Length, info.Length);
        Assert.False(info.SupportsRanges);
    }

    [Fact]
    public async Task Describing_fails_on_an_unsuccessful_status()
    {
        var server = new FakeHttpServer { Intercept = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };

        var ex = await Assert.ThrowsAsync<HttpTransferException>(() => Source(server).GetInfoAsync(Token));

        Assert.Equal(TransferErrorKind.NotFound, ex.Kind);
    }

    [Theory]
    [InlineData(1_000L, 1_000L)]
    [InlineData(5L, 1L)]
    [InlineData(99_000L, null)]
    public async Task Reads_exactly_the_requested_range(long start, long? length)
    {
        var server = new FakeHttpServer();

        using var read = await Source(server).OpenReadAsync(new ReadRequest(new ByteRange(start, length)), Token);
        using var copy = new MemoryStream();
        await read.Content.CopyToAsync(copy, Token);

        var end = length is long count ? start + count : server.Content.Length;
        Assert.Equal(server.Content.Skip((int)start).Take((int)(end - start)).ToArray(), copy.ToArray());
        // The info describes the whole content, not the range
        Assert.Equal(server.Content.Length, read.Info.Length);
    }

    #endregion

    #region Resuming

    [Fact]
    public async Task Continues_after_a_connection_reset_without_downloading_bytes_twice()
    {
        var server = new FakeHttpServer();
        server.FailAfterBytes[1] = 30_000;
        var path = _temp.File("out.bin");

        var result = await RunAsync(Source(server), FileTarget.At(path));

        Assert.Equal(server.Content, File.ReadAllBytes(path));
        Assert.Equal(1, result.Retries);
        var resumed = server.RequestedRanges[1]!.Ranges.Single();
        Assert.Equal(30_000, resumed.From);
        Assert.Null(resumed.To);
    }

    [Fact]
    public async Task Starts_over_when_the_file_changed_before_resuming()
    {
        var server = new FakeHttpServer();
        server.FailAfterBytes[1] = 20_000;
        server.Intercept = _ =>
        {
            // The file changes between the first and the second request
            if (server.RequestCount == 2)
            {
                server.ETag = "\"v2\"";
            }
            return null;
        };
        var path = _temp.File("out.bin");

        var result = await RunAsync(Source(server), FileTarget.At(path));

        // 1: the first attempt, 2: the resume, answered with the new content because of If-Range, 3: starting over
        Assert.Equal([null, 20_000L, null], server.RequestedRanges.Select(RangeStart));
        Assert.Equal(server.Content, File.ReadAllBytes(path));
        Assert.Equal("\"v2\"", result.Source.Version);
    }

    [Fact]
    public async Task Starts_over_from_the_beginning_when_the_server_cant_continue()
    {
        var server = new FakeHttpServer { SupportsRanges = false };
        server.FailAfterBytes[1] = 30_000;
        var path = _temp.File("out.bin");

        await RunAsync(Source(server), FileTarget.At(path));

        Assert.Equal([null, null], server.RequestedRanges.Select(RangeStart));
        Assert.Equal(server.Content, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_range_answered_with_the_whole_content_fails()
    {
        var server = new FakeHttpServer { SupportsRanges = false };

        var ex = await Assert.ThrowsAsync<HttpTransferException>(() => Source(server).OpenReadAsync(new ReadRequest(new ByteRange(1_000, 1_000)), Token));

        Assert.Equal(TransferErrorKind.Protocol, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.Contains("doesn't support ranges", ex.Message);
    }

    #endregion

    #region Errors and timeouts

    [Fact]
    public async Task Every_request_asks_for_the_content_unencoded()
    {
        var server = new FakeHttpServer();
        var encodings = new List<string>();
        server.Intercept = request =>
        {
            lock (encodings)
            {
                encodings.Add(request.Headers.AcceptEncoding.ToString());
            }
            return null;
        };

        await RunAsync(Source(server), FileTarget.At(_temp.File("out.bin")), Options with { MaxConnections = 4 });

        Assert.True(encodings.Count > 1);
        Assert.All(encodings, encoding => Assert.Equal("identity", encoding));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Content_the_server_encoded_anyway_fails_as_unsupported(int connections)
    {
        var server = new FakeHttpServer();
        server.Headers["Content-Encoding"] = "gzip";

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(Source(server), FileTarget.At(_temp.File("out.bin")), Options with { MaxConnections = connections }));

        Assert.Equal(TransferErrorKind.Unsupported, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.Contains("gzip", ex.Message);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Content_marked_as_identity_is_fine()
    {
        var server = new FakeHttpServer();
        server.Headers["Content-Encoding"] = "identity";
        var path = _temp.File("out.bin");

        await RunAsync(Source(server), FileTarget.At(path));

        Assert.Equal(server.Content, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, TransferErrorKind.NotFound, false)]
    [InlineData(HttpStatusCode.Gone, TransferErrorKind.NotFound, false)]
    [InlineData(HttpStatusCode.Unauthorized, TransferErrorKind.AccessDenied, false)]
    [InlineData(HttpStatusCode.Forbidden, TransferErrorKind.AccessDenied, false)]
    [InlineData(HttpStatusCode.RequestTimeout, TransferErrorKind.Timeout, true)]
    [InlineData((HttpStatusCode)429, TransferErrorKind.Network, true)]
    [InlineData(HttpStatusCode.InternalServerError, TransferErrorKind.Network, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, TransferErrorKind.Network, true)]
    [InlineData(HttpStatusCode.NotImplemented, TransferErrorKind.Protocol, false)]
    [InlineData(HttpStatusCode.BadRequest, TransferErrorKind.Protocol, false)]
    public async Task Maps_status_codes_to_errors(HttpStatusCode status, TransferErrorKind kind, bool transient)
    {
        var server = new FakeHttpServer { Intercept = _ => new HttpResponseMessage(status) };

        var ex = await Assert.ThrowsAsync<HttpTransferException>(() => Source(server).OpenReadAsync(new ReadRequest(ByteRange.All), Token));

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(transient, ex.IsTransient);
        Assert.Equal(TransferSide.Source, ex.Side);
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(HttpMethod.Get, ex.Method);
        Assert.Equal(FakeHttpServer.Uri, ex.RequestUri);
        Assert.Contains(((int)status).ToString(), ex.Message);
    }

    [Fact]
    public async Task Retry_after_becomes_the_retry_delay_and_the_status_reaches_the_caller()
    {
        var server = new FakeHttpServer();
        server.Intercept = _ =>
        {
            var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { ReasonPhrase = "Busy" };
            busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
            return busy;
        };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(Source(server), new MemoryTarget(), Options with { Retry = RetryPolicy.None }));

        var http = Assert.IsType<HttpTransferException>(ex.Error);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, http.StatusCode);
        Assert.Equal("Busy", http.ReasonPhrase);
        Assert.Equal(TimeSpan.FromMilliseconds(50), http.RetryDelay);
        Assert.Equal(http.RetryDelay, ex.RetryDelay);
    }

    [Fact]
    public async Task A_request_that_gets_no_response_has_no_status()
    {
        var server = new FakeHttpServer { Intercept = _ => throw new HttpRequestException("Connection refused") };

        var ex = await Assert.ThrowsAsync<HttpTransferException>(() => Source(server).OpenReadAsync(new ReadRequest(ByteRange.All), Token));

        Assert.Null(ex.StatusCode);
        Assert.Equal(TransferErrorKind.Network, ex.Kind);
        Assert.True(ex.IsTransient);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Does_not_retry_a_missing_file()
    {
        var server = new FakeHttpServer { Intercept = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(Source(server), new MemoryTarget()));

        Assert.Equal(TransferErrorKind.NotFound, ex.Kind);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Waits_as_long_as_retry_after_asks_before_retrying()
    {
        var server = new FakeHttpServer();
        server.Intercept = _ =>
        {
            if (server.RequestCount > 1)
            {
                return null;
            }
            var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(300));
            return busy;
        };
        var watch = Stopwatch.StartNew();

        var result = await RunAsync(Source(server), new MemoryTarget());

        Assert.Equal(1, result.Retries);
        Assert.True(watch.ElapsedMilliseconds >= 250, $"Retried after {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Times_out_a_response_that_doesnt_arrive()
    {
        var server = new FakeHttpServer { ResponseDelay = TimeSpan.FromSeconds(30) };
        var options = Options with { ResponseTimeout = TimeSpan.FromMilliseconds(200), Retry = RetryPolicy.None };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(Source(server), new MemoryTarget(), options));

        Assert.Equal(TransferErrorKind.Timeout, ex.Kind);
        Assert.Equal(TransferSide.Source, ex.Side);
    }

    [Fact]
    public async Task A_stalled_body_times_out_and_the_download_continues_where_it_stopped()
    {
        var server = new FakeHttpServer();
        server.Intercept = request => server.RequestCount == 1 ? StallingResponse(server, 10_000, out _) : null;
        var path = _temp.File("out.bin");

        var result = await RunAsync(Source(server), FileTarget.At(path), Options with { IdleTimeout = TimeSpan.FromMilliseconds(300) });

        Assert.Equal(server.Content, File.ReadAllBytes(path));
        Assert.Equal(1, result.Retries);
        Assert.Equal(10_000, RangeStart(server.RequestedRanges[1]));
    }

    [Fact]
    public async Task Canceling_keeps_what_was_received_in_the_partial_file()
    {
        StallingStream? body = null;
        var server = new FakeHttpServer();
        server.Intercept = _ => StallingResponse(server, 10_000, out body);
        var path = _temp.File("out.bin");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var run = RunAsync(Source(server), FileTarget.At(path), cancellationToken: cts.Token);
        while (body == null)
        {
            await Task.Delay(10, Token);
        }
        await body.Stalled.WaitAsync(TimeSpan.FromSeconds(5), Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.False(File.Exists(path));
        var partial = File.ReadAllBytes(path + FileTarget.PartialExtension);
        Assert.Equal(server.Content.AsSpan(0, 10_000).ToArray(), partial.AsSpan(0, 10_000).ToArray());
    }

    [Fact]
    public async Task A_canceled_download_continues_from_its_checkpoint()
    {
        StallingStream? body = null;
        var server = new FakeHttpServer();
        server.Intercept = _ => server.RequestCount == 1 ? StallingResponse(server, 10_000, out body) : null;
        var path = _temp.File("out.bin");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var operation = new TransferClient(Options).From(Source(server)).To(FileTarget.At(path)).Build();
        var run = operation.RunAsync(cancellationToken: cts.Token);
        while (body == null)
        {
            await Task.Delay(10, Token);
        }
        await body.Stalled.WaitAsync(TimeSpan.FromSeconds(5), Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var checkpoint = TransferCheckpoint.Deserialize(operation.GetCheckpoint()!.Serialize());

        var result = await new TransferClient(Options).From(Source(server)).To(FileTarget.At(path)).ResumeFrom(checkpoint).RunAsync(cancellationToken: Token);

        Assert.Equal(server.Content, File.ReadAllBytes(path));
        Assert.Equal(10_000, result.Resumed);
        // 1: the canceled download, 2: describing the content to check it didn't change, 3: the rest
        Assert.Equal(3, server.RequestCount);
        Assert.Equal(10_000, RangeStart(server.RequestedRanges[2]));
    }

    #endregion
}
