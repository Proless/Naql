using System.Net;

namespace Naqel.Core.Tests.Fakes;

/// <summary>
/// An in-memory HTTP server that accepts uploads and records what it received.
/// </summary>
public sealed class FakeUploadServer : HttpMessageHandler
{
    private readonly List<ReceivedUpload> _uploads = [];
    private int _requestCount;

    public static Uri Uri { get; } = new("http://test.local/upload");

    /// <summary>
    /// Answers the n-th request (1-based) after its body was read; return <see langword="null"/> for <c>201 Created</c>.
    /// </summary>
    public Func<int, HttpResponseMessage?>? Respond { get; set; }

    public int RequestCount => Volatile.Read(ref _requestCount);

    public IReadOnlyList<ReceivedUpload> Uploads
    {
        get
        {
            lock (_uploads)
            {
                return _uploads.ToArray();
            }
        }
    }

    public HttpClient CreateClient() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var number = Interlocked.Increment(ref _requestCount);
        var content = request.Content!;

        // Read the length before the body: for a stream of unknown length, reading the header can't compute one
        var upload = new ReceivedUpload
        {
            Method = request.Method,
            Url = request.RequestUri,
            ContentLength = content.Headers.ContentLength,
            ContentType = content.Headers.ContentType?.MediaType,
            UserAgent = request.Headers.UserAgent.ToString()
        };

        if (content is MultipartFormDataContent form)
        {
            var file = form.Single();
            upload.FormField = file.Headers.ContentDisposition?.Name?.Trim('"');
            upload.FormFileName = file.Headers.ContentDisposition?.FileName?.Trim('"');
            upload.FileContentType = file.Headers.ContentType?.MediaType;
            upload.Body = await file.ReadAsByteArrayAsync(cancellationToken);
        }
        else
        {
            upload.Body = await content.ReadAsByteArrayAsync(cancellationToken);
        }

        lock (_uploads)
        {
            _uploads.Add(upload);
        }

        var response = Respond?.Invoke(number) ?? new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"id\":42}") };
        response.RequestMessage = request;
        return response;
    }
}

public sealed class ReceivedUpload
{
    public required HttpMethod Method { get; init; }
    public Uri? Url { get; init; }
    public long? ContentLength { get; init; }
    public string? ContentType { get; init; }
    public string? UserAgent { get; init; }
    public string? FormField { get; set; }
    public string? FormFileName { get; set; }
    public string? FileContentType { get; set; }
    public byte[] Body { get; set; } = [];
}
