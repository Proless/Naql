using System.Net;
using System.Net.Http.Headers;

namespace Naqel.Core.Tests.Fakes;

/// <summary>
/// An in-memory HTTP server for tests. It serves <see cref="Content"/>, supports single byte ranges,
/// and can simulate connection resets or replace responses entirely.
/// </summary>
public sealed class FakeHttpServer : HttpMessageHandler
{
    private readonly List<RangeHeaderValue?> _requestedRanges = new();
    private int _requestCount;

    public static Uri Uri { get; } = new("http://test.local/file.bin");

    public byte[] Content { get; }

    /// <summary>Whether the server honors Range headers and sends "Accept-Ranges: bytes"</summary>
    public bool SupportsRanges { get; set; } = true;

    /// <summary>When set, the server sends a Content-Disposition header with this file name</summary>
    public string? FileName { get; set; }

    /// <summary>The ETag the server sends, or <see langword="null"/> for none; a weak one such as W/"v1" can't be used to resume</summary>
    public string? ETag { get; set; } = "\"v1\"";

    /// <summary>When set, the server sends this Last-Modified date and accepts it in If-Range</summary>
    public DateTimeOffset? LastModified { get; set; }

    /// <summary>Extra response headers, such as Repr-Digest</summary>
    public Dictionary<string, string> Headers { get; } = new();

    /// <summary>Makes the response body of the n-th request (1-based) throw an <see cref="IOException"/> after the given number of bytes</summary>
    public Dictionary<int, int> FailAfterBytes { get; } = new();

    /// <summary>Lets a test replace the response for a request; return <see langword="null"/> to use the normal response</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>How long the server waits before sending the response headers</summary>
    public TimeSpan ResponseDelay { get; set; }

    public int RequestCount => _requestCount;

    public IReadOnlyList<RangeHeaderValue?> RequestedRanges
    {
        get
        {
            lock (_requestedRanges)
            {
                return _requestedRanges.ToArray();
            }
        }
    }

    public FakeHttpServer(int length = 100_000)
    {
        Content = new byte[length];
        new Random(42).NextBytes(Content);
    }

    public HttpClient CreateClient() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestNumber = Interlocked.Increment(ref _requestCount);

        lock (_requestedRanges)
        {
            _requestedRanges.Add(request.Headers.Range);
        }

        if (ResponseDelay > TimeSpan.Zero)
        {
            await Task.Delay(ResponseDelay, cancellationToken);
        }

        var response = Intercept?.Invoke(request) ?? CreateResponse(request, requestNumber);
        response.RequestMessage = request;
        return response;
    }

    private HttpResponseMessage CreateResponse(HttpRequestMessage request, int requestNumber)
    {
        long from = 0;
        long to = Content.Length - 1;
        var status = HttpStatusCode.OK;

        // With If-Range, a changed file is answered with the whole new content instead of the range
        var ifRange = request.Headers.IfRange;
        var rangeStillValid = ifRange == null || (ifRange.EntityTag is { } tag ? !tag.IsWeak && tag.ToString() == ETag : ifRange.Date == LastModified);

        if (SupportsRanges && rangeStillValid && request.Headers.Range is { } range)
        {
            var item = range.Ranges.Single();
            if (item.From is null)
            {
                from = Math.Max(0, Content.Length - item.To!.Value);
            }
            else
            {
                from = item.From.Value;
                to = Math.Min(item.To ?? to, to);
            }

            if (from > to)
            {
                var unsatisfiable = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
                unsatisfiable.Content.Headers.ContentRange = new ContentRangeHeaderValue(Content.Length);
                return unsatisfiable;
            }

            status = HttpStatusCode.PartialContent;
        }

        Stream body = new MemoryStream(Content, (int)from, (int)(to - from + 1), writable: false);
        if (FailAfterBytes.TryGetValue(requestNumber, out var failAfter))
        {
            body = new FaultyStream(body, failAfter);
        }

        return CreateResponse(status, from, to, body);
    }

    private HttpResponseMessage CreateResponse(HttpStatusCode status, long from, long to, Stream body)
    {
        var response = new HttpResponseMessage(status) { Content = new StreamContent(body) };
        response.Content.Headers.ContentLength = to - from + 1;
        if (ETag != null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(ETag);
        }
        response.Content.Headers.LastModified = LastModified;

        if (SupportsRanges)
        {
            response.Headers.AcceptRanges.Add("bytes");
        }

        if (status == HttpStatusCode.PartialContent)
        {
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, Content.Length);
        }

        foreach (var (name, value) in Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (FileName != null)
        {
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = FileName };
        }

        return response;
    }

    /// <summary>
    /// A read-only stream that throws like a reset connection after a number of bytes.
    /// </summary>
    private sealed class FaultyStream(Stream inner, int failAfterBytes) : Stream
    {
        private int _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= failAfterBytes)
            {
                throw new IOException("Simulated connection reset");
            }

            var read = inner.Read(buffer, offset, Math.Min(count, failAfterBytes - _read));
            _read += read;
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
