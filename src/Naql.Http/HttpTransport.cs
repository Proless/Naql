using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Http;

/// <summary>
/// The HTTP client and request settings that HTTP sources share.
/// </summary>
/// <remarks>
/// Timeouts and retries belong to the transfer, so the client should have <see cref="HttpClient.Timeout"/> set to
/// <see cref="Timeout.InfiniteTimeSpan"/>; <see cref="Default"/> does.
/// </remarks>
public sealed class HttpTransport : IDisposable
{
#if NET8_0_OR_GREATER
    // A long-lived client that still notices DNS changes, because pooled connections are replaced every few minutes
    private static readonly Lazy<HttpTransport> SharedTransport = new(() => new HttpTransport(
        new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan }));
#else
    // netstandard2.1 can't name SocketsHttpHandler, so pooled connections live until they idle out; an app that must
    // notice DNS changes on .NET 5 to 7 passes its own client
    private static readonly Lazy<HttpTransport> SharedTransport = new(() => new HttpTransport(
        new HttpClient(new HttpClientHandler()) { Timeout = Timeout.InfiniteTimeSpan }));
#endif

    private readonly bool _disposeClient;

    /// <summary>
    /// Uses <paramref name="client"/> for every request.
    /// </summary>
    /// <param name="client">The client to send requests with</param>
    /// <param name="disposeClient">Whether disposing the transport disposes the client</param>
    public HttpTransport(HttpClient client, bool disposeClient = false)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _disposeClient = disposeClient;
    }

    /// <summary>
    /// A transport with a shared <see cref="HttpClient"/> and no extra settings.
    /// </summary>
    public static HttpTransport Default => SharedTransport.Value;

    /// <summary>
    /// The <c>User-Agent</c> of this library, the operating system and the runtime.
    /// </summary>
    public static string DefaultUserAgent
    {
        get
        {
            var assembly = typeof(HttpTransport).Assembly.GetName();
            var comment = $"{RuntimeInformation.OSDescription}; {RuntimeInformation.OSArchitecture}; {RuntimeInformation.FrameworkDescription}";
            return $"{assembly.Name}/{assembly.Version?.ToString(3)} ({comment})";
        }
    }

    /// <summary>
    /// The client requests are sent with.
    /// </summary>
    public HttpClient Client { get; }

    /// <summary>
    /// The <c>User-Agent</c> header sent with every request, or <c>null</c> to send none.
    /// </summary>
    public string? UserAgent { get; init; } = DefaultUserAgent;

    /// <summary>
    /// Changes every request before it is sent, for example to add authentication or other headers.
    /// </summary>
    public Func<HttpRequestMessage, CancellationToken, Task>? ConfigureRequest { get; init; }

    /// <summary>
    /// A <see cref="ConfigureRequest"/> that adds HTTP basic authentication.
    /// </summary>
    public static Func<HttpRequestMessage, CancellationToken, Task> BasicAuthentication(string userName, string password)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));
        return (request, _) =>
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// A source that downloads <paramref name="uri"/> with this transport.
    /// </summary>
    public HttpSource Get(Uri uri) => new(this, uri);

    /// <summary>
    /// A source that downloads <paramref name="uri"/> with this transport.
    /// </summary>
    public HttpSource Get(string uri) => Get(ToUri(uri));

    /// <summary>
    /// A target that uploads the content as the body of a <c>PUT</c> request to <paramref name="uri"/>.
    /// </summary>
    public HttpTarget Put(Uri uri) => new(this, uri, HttpMethod.Put, formField: null);

    /// <summary>
    /// A target that uploads the content as the body of a <c>PUT</c> request to <paramref name="uri"/>.
    /// </summary>
    public HttpTarget Put(string uri) => Put(ToUri(uri));

    /// <summary>
    /// A target that uploads the content as the body of a <c>POST</c> request to <paramref name="uri"/>.
    /// </summary>
    public HttpTarget Post(Uri uri) => new(this, uri, HttpMethod.Post, formField: null);

    /// <summary>
    /// A target that uploads the content as the body of a <c>POST</c> request to <paramref name="uri"/>.
    /// </summary>
    public HttpTarget Post(string uri) => Post(ToUri(uri));

    /// <summary>
    /// A target that uploads the content as a file field of a <c>multipart/form-data</c> <c>POST</c>, like an HTML form.
    /// </summary>
    /// <param name="uri">Where to post the form</param>
    /// <param name="fieldName">The name of the form field that holds the file</param>
    public HttpTarget PostForm(Uri uri, string fieldName = "file") => new(this, uri, HttpMethod.Post, fieldName ?? throw new ArgumentNullException(nameof(fieldName)));

    /// <summary>
    /// A target that uploads the content as a file field of a <c>multipart/form-data</c> <c>POST</c>, like an HTML form.
    /// </summary>
    /// <param name="uri">Where to post the form</param>
    /// <param name="fieldName">The name of the form field that holds the file</param>
    public HttpTarget PostForm(string uri, string fieldName = "file") => PostForm(ToUri(uri), fieldName);

    private static Uri ToUri(string uri) => new(uri ?? throw new ArgumentNullException(nameof(uri)));

    /// <summary>
    /// Disposes the client when the transport owns it.
    /// </summary>
    public void Dispose()
    {
        if (_disposeClient)
        {
            Client.Dispose();
        }
    }

    internal static void EnsureHttpScheme(Uri uri)
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }
        if (!uri.IsAbsoluteUri || !(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"'{uri}' isn't an absolute http or https URL", nameof(uri));
        }
    }

    /// <summary>
    /// Applies the settings and sends the request, returning once the response headers have arrived.
    /// </summary>
    internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, TransferSide side, CancellationToken cancellationToken)
    {
        if (UserAgent != null)
        {
            request.Headers.UserAgent.TryParseAdd(UserAgent);
        }
        if (ConfigureRequest != null)
        {
            await ConfigureRequest(request, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpTransferException(ex.Message, TransferErrorKind.Network, isTransient: true, side, request.Method, request.RequestUri!, statusCode: StatusOf(ex), innerException: ex);
        }
    }

    // HttpRequestException carries a status from .NET 5 on, which netstandard2.1 can't read
#if NET8_0_OR_GREATER
    private static System.Net.HttpStatusCode? StatusOf(HttpRequestException ex) => ex.StatusCode;
#else
    private static System.Net.HttpStatusCode? StatusOf(HttpRequestException ex) => null;
#endif
}
