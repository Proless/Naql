using Naql.Http;
using System.Globalization;
using System.Net;

namespace Naql.Tests.Fakes;

/// <summary>
/// An HTTP upload service in the style of tus: <c>POST /uploads</c> starts a session, <c>PATCH</c> appends a chunk
/// at <c>Upload-Offset</c>, <c>HEAD</c> tells where a session stands, and <c>DELETE</c> cancels it.
/// </summary>
public sealed class FakeChunkedService : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Upload> _uploads = [];
    private int _patches;
    private int _started;

    public static Uri Start { get; } = new("http://test.local/uploads");

    /// <summary>Runs before a chunk is taken, with the number of the PATCH; return a response to answer with it instead, or throw.</summary>
    public Func<int, HttpResponseMessage?>? OnPatch { get; set; }

    /// <summary>The most bytes the server takes of a chunk; it says so in its answer, and expects the rest again.</summary>
    public int? TakeAtMost { get; set; }

    public int Patches => Volatile.Read(ref _patches);

    public List<long> PatchOffsets { get; } = [];

    public sealed class Upload
    {
        public MemoryStream Data { get; } = new();
        public bool Finished { get; set; }
        public bool Deleted { get; set; }
        public long? Length { get; set; }
    }

    public IReadOnlyDictionary<string, Upload> Uploads
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, Upload>(_uploads);
            }
        }
    }

    /// <summary>Makes the server forget an upload, as if its session expired.</summary>
    public void Forget(string url)
    {
        lock (_lock)
        {
            _uploads.Remove(url);
        }
    }

    public HttpClient CreateClient() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        HttpResponseMessage response;
        if (request.Method == HttpMethod.Post && url == Start.AbsoluteUri)
        {
            lock (_lock)
            {
                var location = $"{Start.AbsoluteUri}/{++_started}";
                _uploads[location] = new Upload();
                response = new HttpResponseMessage(HttpStatusCode.Created) { Headers = { Location = new Uri(location) } };
            }
        }
        else if (Find(url) is not { } upload)
        {
            response = new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        else if (request.Method == HttpMethod.Head)
        {
            response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("Upload-Offset", upload.Data.Length.ToString(CultureInfo.InvariantCulture));
        }
        else if (request.Method == HttpMethod.Delete)
        {
            upload.Deleted = true;
            response = new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        else
        {
            response = await PatchAsync(upload, url, request, cancellationToken);
        }
        response.RequestMessage = request;
        return response;
    }

    private async Task<HttpResponseMessage> PatchAsync(Upload upload, string url, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var patch = Interlocked.Increment(ref _patches) - 1;
        var offset = long.Parse(request.Headers.GetValues("Upload-Offset").Single(), CultureInfo.InvariantCulture);
        lock (PatchOffsets)
        {
            PatchOffsets.Add(offset);
        }
        var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        if (OnPatch?.Invoke(patch) is { } replaced)
        {
            return replaced;
        }

        lock (_lock)
        {
            if (offset != upload.Data.Length)
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            }
            var take = Math.Min(body.Length, TakeAtMost ?? int.MaxValue);
            upload.Data.Write(body, 0, take);
            if (request.Headers.TryGetValues("Upload-Length", out var lengths))
            {
                upload.Length = long.Parse(lengths.Single(), CultureInfo.InvariantCulture);
            }
            upload.Finished = upload.Length == upload.Data.Length;

            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            response.Headers.Add("Upload-Offset", upload.Data.Length.ToString(CultureInfo.InvariantCulture));
            if (upload.Finished)
            {
                response.Headers.Add("File-Location", url.Replace("/uploads/", "/files/"));
            }
            return response;
        }
    }

    private Upload? Find(string url)
    {
        lock (_lock)
        {
            return _uploads.TryGetValue(url, out var upload) ? upload : null;
        }
    }
}

/// <summary>
/// A target for <see cref="FakeChunkedService"/>, written the way a package for a real service would be.
/// </summary>
public sealed class TusLikeTarget(HttpTransport transport, int chunkSize) : ChunkedUploadTarget(transport, chunkSize)
{
    public int Queries { get; private set; }

    protected override async Task StartAsync(ChunkedUpload upload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, FakeChunkedService.Start);
        using var response = await Transport.SendAsync(request, TransferSide.Target, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw HttpTransferException.FromResponse(response, TransferSide.Target);
        }
        upload.Token = response.Headers.Location!.AbsoluteUri;
    }

    protected override async Task<long> SendChunkAsync(ChunkedUpload upload, UploadChunk chunk, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PATCH"), upload.Token)
        {
            Content = new ByteArrayContent(chunk.Content.Array!, chunk.Content.Offset, chunk.Count)
        };
        request.Headers.Add("Upload-Offset", chunk.Offset.ToString(CultureInfo.InvariantCulture));
        if (chunk.IsLast)
        {
            request.Headers.Add("Upload-Length", chunk.TotalLength!.Value.ToString(CultureInfo.InvariantCulture));
        }

        using var response = await Transport.SendAsync(request, TransferSide.Target, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw HttpTransferException.FromResponse(response, TransferSide.Target);
        }
        if (response.Headers.TryGetValues("File-Location", out var locations))
        {
            upload.Location = locations.Single();
            upload.Metadata["id"] = upload.Location.Substring(upload.Location.LastIndexOf('/') + 1);
        }
        return long.Parse(response.Headers.GetValues("Upload-Offset").Single(), CultureInfo.InvariantCulture);
    }

    protected override async Task<long> QueryAsync(ChunkedUpload upload, CancellationToken cancellationToken)
    {
        Queries++;
        using var request = new HttpRequestMessage(HttpMethod.Head, upload.Token);
        using var response = await Transport.SendAsync(request, TransferSide.Target, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw HttpTransferException.FromResponse(response, TransferSide.Target);
        }
        return long.Parse(response.Headers.GetValues("Upload-Offset").Single(), CultureInfo.InvariantCulture);
    }

    protected override async Task AbortAsync(ChunkedUpload upload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, upload.Token);
        using var response = await Transport.SendAsync(request, TransferSide.Target, cancellationToken);
    }
}
