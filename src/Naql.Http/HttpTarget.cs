using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Http;

/// <summary>
/// Uploads the content to an HTTP or HTTPS URL in a single request.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The body is streamed, never buffered. It has a <c>Content-Length</c> when the length is known, and is sent in
/// chunks otherwise.</item>
/// <item>The <c>Content-Type</c> is the source's, or <c>application/octet-stream</c>.</item>
/// <item>A failed upload is sent again from the start; HTTP has no standard way to continue one. A <c>POST</c> is sent
/// again only if it failed before the whole body was sent, because the server may have processed a complete one.</item>
/// <item>The upload is done when the server answers with a success status. Once the body is sent, the response
/// timeout covers the wait for that answer.</item>
/// </list>
/// </remarks>
public sealed class HttpTarget : ITransferTarget
{
    private const string DefaultContentType = "application/octet-stream";
    private const string DefaultFormFileName = "file";
    private const int CopyBufferSize = 81920;

    private readonly Func<HttpResponseMessage, CancellationToken, Task>? _responseHandler;

    internal HttpTarget(HttpTransport transport, Uri uri, HttpMethod method, string? formField, Func<HttpResponseMessage, CancellationToken, Task>? responseHandler = null)
    {
        HttpTransport.EnsureHttpScheme(uri);
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Uri = uri;
        Method = method;
        FormField = formField;
        _responseHandler = responseHandler;
        Capabilities = new TargetCapabilities { RepeatableWrites = method == HttpMethod.Put };
    }

    /// <summary>
    /// Uploads to <paramref name="uri"/> with a <c>PUT</c> request, using <see cref="HttpTransport.Default"/>.
    /// </summary>
    public static HttpTarget Put(Uri uri) => HttpTransport.Default.Put(uri);

    /// <summary>
    /// Uploads to <paramref name="uri"/> with a <c>PUT</c> request, using <see cref="HttpTransport.Default"/>.
    /// </summary>
    public static HttpTarget Put(string uri) => HttpTransport.Default.Put(uri);

    /// <summary>
    /// Uploads to <paramref name="uri"/> with a <c>POST</c> request, using <see cref="HttpTransport.Default"/>.
    /// </summary>
    public static HttpTarget Post(Uri uri) => HttpTransport.Default.Post(uri);

    /// <summary>
    /// Uploads to <paramref name="uri"/> with a <c>POST</c> request, using <see cref="HttpTransport.Default"/>.
    /// </summary>
    public static HttpTarget Post(string uri) => HttpTransport.Default.Post(uri);

    /// <summary>
    /// Uploads to <paramref name="uri"/> as a <c>multipart/form-data</c> file field, using <see cref="HttpTransport.Default"/>.
    /// </summary>
    public static HttpTarget PostForm(Uri uri, string fieldName = "file") => HttpTransport.Default.PostForm(uri, fieldName);

    /// <summary>
    /// Uploads to <paramref name="uri"/> as a <c>multipart/form-data</c> file field, using <see cref="HttpTransport.Default"/>.
    /// </summary>
    public static HttpTarget PostForm(string uri, string fieldName = "file") => HttpTransport.Default.PostForm(uri, fieldName);

    /// <summary>
    /// The URL to upload to.
    /// </summary>
    public Uri Uri { get; }

    /// <summary>
    /// The request method.
    /// </summary>
    public HttpMethod Method { get; }

    /// <summary>
    /// The form field that holds the file in a <c>multipart/form-data</c> upload, or <c>null</c> for a plain body.
    /// </summary>
    public string? FormField { get; }

    /// <summary>
    /// The client and request settings.
    /// </summary>
    public HttpTransport Transport { get; }

    /// <summary>
    /// One request carries the whole content, so the target takes neither parallel nor partial parts. Writes are
    /// repeatable for <c>PUT</c>, which replaces what's there, and not for <c>POST</c>.
    /// </summary>
    public TargetCapabilities Capabilities { get; }

    /// <summary>
    /// Returns a target that also passes the server's successful response to <paramref name="handler"/>, for example to
    /// read the location or id of the uploaded file. An exception from the handler fails the upload.
    /// </summary>
    public HttpTarget WithResponseHandler(Func<HttpResponseMessage, CancellationToken, Task> handler)
    {
        return new HttpTarget(Transport, Uri, Method, FormField, handler ?? throw new ArgumentNullException(nameof(handler)));
    }

    /// <inheritdoc />
    public Task<ITargetSession> OpenAsync(TargetOpenRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult<ITargetSession>(new UploadSession(this, (request ?? throw new ArgumentNullException(nameof(request))).Source));
    }

    private HttpContent CreateContent(Stream body, SourceInfo source)
    {
        var content = new UploadContent(body, source.Length);
        content.Headers.ContentType = MediaTypeHeaderValue.TryParse(source.ContentType, out var type)
            ? type
            : new MediaTypeHeaderValue(DefaultContentType);

        if (FormField == null)
        {
            return content;
        }

        var form = new MultipartFormDataContent();
        form.Add(content, FormField, source.Name ?? DefaultFormFileName);
        return form;
    }

    /// <summary>
    /// Streams the body and reports its length, so a form around it has a <c>Content-Length</c> too instead of being
    /// sent in chunks, which some servers refuse.
    /// </summary>
    private sealed class UploadContent(Stream body, long? length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            return body.CopyToAsync(stream, CopyBufferSize, cancellationToken);
        }

        protected override bool TryComputeLength(out long result)
        {
            result = length ?? 0;
            return length.HasValue;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                body.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class UploadSession(HttpTarget target, SourceInfo source) : ITargetSession
    {
        public string? ResumeToken => null;

        public async Task WritePartAsync(ByteRange range, Stream content, CancellationToken cancellationToken)
        {
            if (!range.IsAll)
            {
                throw new TransferException($"An HTTP upload sends the whole content in one request, not the bytes {range}", TransferErrorKind.Unsupported, side: TransferSide.Target);
            }

            using var request = new HttpRequestMessage(target.Method, target.Uri) { Content = target.CreateContent(content, source) };
            using var response = await target.Transport.SendAsync(request, TransferSide.Target, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw HttpErrors.FromStatus(response, TransferSide.Target);
            }
            if (target._responseHandler != null)
            {
                await target._responseHandler(response, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task CompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        // An upload the server accepted can't be taken back
        public Task AbortAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
