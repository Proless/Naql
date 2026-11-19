using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Http;

/// <summary>
/// Downloads the content of an HTTP or HTTPS URL with GET requests.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Ranges use the <c>Range</c> header; a resumed range sends <c>If-Range</c>, so a changed file is noticed.</item>
/// <item>The version of the content is its strong ETag, or else its <c>Last-Modified</c> date.</item>
/// <item>The suggested name comes from <c>Content-Disposition</c>, or else from the last segment of the final URL.</item>
/// <item>Every request asks for the content unencoded (<c>Accept-Encoding: identity</c>), because ranges, lengths and
/// checksums describe the bytes as sent. A response the server encoded anyway, such as with <c>gzip</c>, fails as
/// <see cref="TransferErrorKind.Unsupported"/>. Don't give the transport an <see cref="HttpClient"/> with automatic
/// decompression: it would decode the bytes without saying so.</item>
/// </list>
/// </remarks>
public sealed class HttpSource : ITransferSource
{
    internal HttpSource(HttpTransport transport, Uri uri)
    {
        HttpTransport.EnsureHttpScheme(uri);
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Uri = uri;
    }

    /// <summary>
    /// Downloads <paramref name="uri"/> with <see cref="HttpTransport.Default"/>.
    /// </summary>
    public static HttpSource Get(Uri uri) => HttpTransport.Default.Get(uri);

    /// <summary>
    /// Downloads <paramref name="uri"/> with <see cref="HttpTransport.Default"/>.
    /// </summary>
    public static HttpSource Get(string uri) => HttpTransport.Default.Get(uri);

    /// <summary>
    /// The URL to download.
    /// </summary>
    public Uri Uri { get; }

    /// <summary>
    /// The client and request settings.
    /// </summary>
    public HttpTransport Transport { get; }

    /// <summary>
    /// Asks for the first byte only: the answer tells the length and whether the server supports ranges, which a
    /// <c>HEAD</c> request often doesn't.
    /// </summary>
    public async Task<SourceInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(new RangeHeaderValue(0, 0), ifVersion: null);
        using var response = await Transport.SendAsync(request, TransferSide.Source, cancellationToken).ConfigureAwait(false);
        EnsureNotEncoded(response);

        var contentRange = response.Content.Headers.ContentRange;
        switch (response.StatusCode)
        {
            case HttpStatusCode.PartialContent:
                return Describe(response, contentRange?.Length, supportsRanges: true);
            case HttpStatusCode.RequestedRangeNotSatisfiable when contentRange?.Length == 0:
                // Even the first byte is out of range: the content is empty
                return Describe(response, 0, supportsRanges: true);
            case HttpStatusCode.OK:
                // The server ignored the range, so it can't split the content or continue it
                return Describe(response, response.Content.Headers.ContentLength, supportsRanges: false);
            default:
                throw StatusError(response);
        }
    }

    /// <inheritdoc />
    public async Task<SourceStream> OpenReadAsync(ReadRequest request, CancellationToken cancellationToken)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var range = request.Range;
        var rangeHeader = range.IsAll ? null : new RangeHeaderValue(range.Start, range.End);
        if (range.Length == 0)
        {
            throw new ArgumentException("Can't request an empty range", nameof(request));
        }

        using var httpRequest = CreateRequest(rangeHeader, request.IfVersion);
        var response = await Transport.SendAsync(httpRequest, TransferSide.Source, cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotEncoded(response);
            SourceInfo info;
            var contentRange = response.Content.Headers.ContentRange;
            if (response.StatusCode == HttpStatusCode.PartialContent && rangeHeader != null)
            {
                if (contentRange?.From != range.Start)
                {
                    throw HttpErrors.FromResponse(response, $"The server sent bytes from {contentRange?.From} instead of {range.Start}", TransferErrorKind.Protocol, transient: true, TransferSide.Source);
                }
                info = Describe(response, contentRange.Length, supportsRanges: true);
                EnsureSameVersion(response, request.IfVersion, info);
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                info = Describe(response, response.Content.Headers.ContentLength, AcceptsRanges(response));
                // A server that supports If-Range answers a changed file with the whole new content
                EnsureSameVersion(response, request.IfVersion, info);
                if (rangeHeader != null)
                {
                    throw HttpErrors.FromResponse(response, $"The server sent the whole content instead of the bytes {range}; it doesn't support ranges", TransferErrorKind.Protocol, transient: false, TransferSide.Source);
                }
            }
            else
            {
                throw StatusError(response);
            }

#if NET8_0_OR_GREATER
            var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
            // The headers have arrived, so this returns the response stream right away
            var content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
            return new SourceStream(new ResponseStream(content, response), info);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private HttpRequestMessage CreateRequest(RangeHeaderValue? range, string? ifVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Uri);
        request.Headers.Range = range;
        // Ranges, lengths and checksums describe the bytes as sent, so ask for them as they are, not compressed
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (range != null && ifVersion != null)
        {
            request.Headers.IfRange = ParseVersion(ifVersion);
        }
        return request;
    }

    /// <summary>
    /// Fails for content the server encoded, such as with <c>gzip</c>, although the request asked for it as it is:
    /// its bytes, length and ranges wouldn't be the content's.
    /// </summary>
    private static void EnsureNotEncoded(HttpResponseMessage response)
    {
        var encodings = response.Content.Headers.ContentEncoding;
        if (response.IsSuccessStatusCode && encodings.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw HttpErrors.FromResponse(response, $"The server sent the content encoded as {string.Join(", ", encodings)} although it was asked for it unencoded",
                TransferErrorKind.Unsupported, transient: false, TransferSide.Source);
        }
    }

    private static RangeConditionHeaderValue? ParseVersion(string version)
    {
        if (EntityTagHeaderValue.TryParse(version, out var etag))
        {
            return new RangeConditionHeaderValue(etag);
        }
        return DateTimeOffset.TryParse(version, out var date) ? new RangeConditionHeaderValue(date) : null;
    }

    private static void EnsureSameVersion(HttpResponseMessage response, string? expected, SourceInfo info)
    {
        if (expected != null && info.Version != null && expected != info.Version)
        {
            throw HttpErrors.FromResponse(response, $"The content changed from version {expected} to {info.Version}", TransferErrorKind.SourceChanged, transient: false, TransferSide.Source);
        }
    }

    private static bool AcceptsRanges(HttpResponseMessage response)
    {
        return response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase);
    }

    private SourceInfo Describe(HttpResponseMessage response, long? length, bool supportsRanges)
    {
        var headers = response.Content.Headers;
        var etag = response.Headers.ETag;
        var version = etag is { IsWeak: false }
            ? etag.ToString()
            : headers.LastModified?.ToString("R");

        return new SourceInfo
        {
            Length = length,
            SupportsRanges = supportsRanges,
            IsRepeatable = true,
            Identity = Uri.AbsoluteUri,
            Version = version,
            Name = GetName(response),
            ContentType = headers.ContentType?.ToString(),
            LastModified = headers.LastModified,
            Checksum = GetChecksum(response)
        };
    }

    /// <summary>
    /// The hash of the whole content from <c>Repr-Digest</c> (RFC 9530), or else the older <c>Digest</c> (RFC 3230).
    /// Both describe the whole representation, so they also hold for a range response. SHA-512 wins over SHA-256.
    /// </summary>
    private static Checksum? GetChecksum(HttpResponseMessage response)
    {
        return ParseDigests(HeaderValues(response, "Repr-Digest"), colons: true)
            ?? ParseDigests(HeaderValues(response, "Digest"), colons: false);
    }

    private static string[] HeaderValues(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? values.ToArray()
            : [];
    }

    /// <param name="values">The header values, each a comma-separated list of <c>algorithm=hash</c></param>
    /// <param name="colons">Whether the Base64 hash is wrapped in colons, as structured fields write byte sequences</param>
    private static Checksum? ParseDigests(string[] values, bool colons)
    {
        Checksum? sha256 = null;
        foreach (var entry in values.SelectMany(value => value.Split(',')))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var algorithm = entry[..separator].Trim().ToLowerInvariant() switch
            {
                "sha-512" => HashAlgorithmName.SHA512,
                "sha-256" => HashAlgorithmName.SHA256,
                _ => default
            };
            var hash = entry[(separator + 1)..].Trim();
            if (algorithm == default || (colons && !(hash.Length > 2 && hash[0] == ':' && hash[^1] == ':')))
            {
                continue;
            }

            try
            {
                var checksum = Checksum.FromBase64(algorithm, colons ? hash[1..^1] : hash);
                if (algorithm == HashAlgorithmName.SHA512)
                {
                    return checksum;
                }
                sha256 ??= checksum;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                // A malformed digest is ignored, like any header the client can't use
            }
        }
        return sha256;
    }

    private string? GetName(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var name = !string.IsNullOrWhiteSpace(disposition?.FileNameStar) ? disposition!.FileNameStar : disposition?.FileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            // The last segment of the URL after redirects
            var uri = response.RequestMessage?.RequestUri ?? Uri;
            name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        }

        name = name?.Trim().Trim('"');
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static TransferException StatusError(HttpResponseMessage response) => HttpErrors.FromStatus(response, TransferSide.Source);

    /// <summary>
    /// The response body; disposing it also disposes the response.
    /// </summary>
    private sealed class ResponseStream(Stream content, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return content.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return content.ReadAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => content.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => content.Read(buffer);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                content.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
