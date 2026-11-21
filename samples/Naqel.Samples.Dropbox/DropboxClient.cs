using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Naqel.Http;

namespace Naqel.Samples.Dropbox;

/// <summary>
/// Calls the Dropbox API: the endpoints the resolvers, sources and targets of this sample need, with Dropbox's errors
/// turned into errors the engine understands.
/// </summary>
/// <remarks>
/// Two transports, because Dropbox's temporary download links need no token: <see cref="Api"/> signs every request
/// with the account's token, <see cref="Links"/> sends none, so the token never goes to the download host.
/// </remarks>
public sealed class DropboxClient
{
    /// <summary>
    /// Where the API's RPC endpoints are.
    /// </summary>
    public static Uri DefaultApiBase { get; } = new("https://api.dropboxapi.com/2/");

    /// <summary>
    /// Where the API's upload and download endpoints are.
    /// </summary>
    public static Uri DefaultContentBase { get; } = new("https://content.dropboxapi.com/2/");

    /// <summary>
    /// A client that sends API calls with <paramref name="api"/>, which must authenticate them, and downloads from
    /// temporary links with <paramref name="links"/>.
    /// </summary>
    /// <param name="api">Sends API calls, such as with a <see cref="BearerTokenHandler"/></param>
    /// <param name="links">Downloads from temporary links, without a token</param>
    /// <param name="apiBase">The RPC endpoints, <see cref="DefaultApiBase"/> unless testing</param>
    /// <param name="contentBase">The upload endpoints, <see cref="DefaultContentBase"/> unless testing</param>
    public DropboxClient(HttpTransport api, HttpTransport links, Uri? apiBase = null, Uri? contentBase = null)
    {
        Api = api ?? throw new ArgumentNullException(nameof(api));
        Links = links ?? throw new ArgumentNullException(nameof(links));
        ApiBase = apiBase ?? DefaultApiBase;
        ContentBase = contentBase ?? DefaultContentBase;
    }

    /// <summary>
    /// A client for an account whose OAuth tokens come from <paramref name="getToken"/>, as
    /// <see cref="BearerTokenHandler"/> asks for them: the current one, or with <c>true</c> a fresh one after Dropbox
    /// rejected it. Signing in to get the first one is the app's.
    /// </summary>
    /// <param name="getToken">Returns the account's current token, or a fresh one</param>
    /// <param name="handler">Sends the requests, such as a test's fake server; a new <see cref="SocketsHttpHandler"/>
    /// when <c>null</c></param>
    /// <param name="apiBase">The RPC endpoints, <see cref="DefaultApiBase"/> unless testing</param>
    /// <param name="contentBase">The upload endpoints, <see cref="DefaultContentBase"/> unless testing</param>
    public static DropboxClient Create(Func<bool, CancellationToken, Task<string>> getToken, HttpMessageHandler? handler = null,
        Uri? apiBase = null, Uri? contentBase = null)
    {
        handler ??= new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        // The engine has its own timeouts, which also cover large chunks on slow connections
#pragma warning disable CA2000 // The clients own the handlers and live as long as the app's client does
        var api = new HttpClient(new BearerTokenHandler(getToken, handler), disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var links = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
#pragma warning restore CA2000
        return new DropboxClient(new HttpTransport(api), new HttpTransport(links), apiBase, contentBase);
    }

    /// <summary>
    /// Sends API calls, authenticated.
    /// </summary>
    public HttpTransport Api { get; }

    /// <summary>
    /// Downloads from temporary links, without a token.
    /// </summary>
    public HttpTransport Links { get; }

    /// <summary>
    /// Where the RPC endpoints are.
    /// </summary>
    public Uri ApiBase { get; }

    /// <summary>
    /// Where the upload endpoints are.
    /// </summary>
    public Uri ContentBase { get; }

    /// <summary>
    /// The file or folder at <paramref name="path"/>, a Dropbox path or an <c>id:</c>.
    /// </summary>
    /// <exception cref="TransferException">Nothing is there (<see cref="TransferErrorKind.NotFound"/>), or the call failed</exception>
    public async Task<DropboxEntry> GetMetadataAsync(string path, CancellationToken cancellationToken)
    {
        if (path.Length == 0)
        {
            // The API can't describe the root, which is always a folder
            return DropboxEntry.Root;
        }
        var reply = await CallAsync("files/get_metadata", new { path }, TransferSide.Source, cancellationToken).ConfigureAwait(false);
        return DropboxEntry.Parse(reply);
    }

    /// <summary>
    /// The files in the folder <paramref name="path"/>, page by page as Dropbox lists them, and with
    /// <paramref name="recursive"/> those of its subfolders.
    /// </summary>
    public async IAsyncEnumerable<DropboxEntry> ListFilesAsync(string path, bool recursive, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = await CallAsync("files/list_folder", new { path, recursive }, TransferSide.Source, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            foreach (var item in page.GetProperty("entries").EnumerateArray())
            {
                var entry = DropboxEntry.Parse(item);
                if (entry.IsFile)
                {
                    yield return entry;
                }
            }
            if (!page.GetProperty("has_more").GetBoolean())
            {
                yield break;
            }
            var cursor = page.GetProperty("cursor").GetString();
            page = await CallAsync("files/list_folder/continue", new { cursor }, TransferSide.Source, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A link to download the file at <paramref name="path"/> from, with ranges and without a token, for four hours.
    /// </summary>
    public async Task<Uri> GetTemporaryLinkAsync(string path, CancellationToken cancellationToken)
    {
        var reply = await CallAsync("files/get_temporary_link", new { path }, TransferSide.Source, cancellationToken).ConfigureAwait(false);
        return new Uri(reply.GetProperty("link").GetString()!);
    }

    /// <summary>
    /// Calls an RPC endpoint with JSON arguments.
    /// </summary>
    /// <returns>The JSON answer</returns>
    /// <exception cref="TransferException">The call failed</exception>
    internal async Task<JsonElement> CallAsync(string endpoint, object arguments, TransferSide side, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase, endpoint))
        {
            Content = new StringContent(JsonSerializer.Serialize(arguments), Encoding.UTF8, "application/json")
        };
        var reply = await SendAsync(request, side, cancellationToken).ConfigureAwait(false);
        return reply.Error is { } error ? throw error : reply.Body;
    }

    /// <summary>
    /// Calls an upload endpoint, with the arguments in the <c>Dropbox-API-Arg</c> header and <paramref name="body"/>
    /// as the content.
    /// </summary>
    /// <returns>The answer, which the caller checks for an error it handles itself, such as a wrong offset</returns>
    internal async Task<DropboxReply> UploadAsync(string endpoint, object arguments, ArraySegment<byte> body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ContentBase, endpoint))
        {
            Content = new ByteArrayContent(body.Array ?? [], body.Offset, body.Count)
        };
        request.Content.Headers.ContentType = new("application/octet-stream");
        // A header must be ASCII; System.Text.Json escapes everything else, such as accented names, as \u sequences
        request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", JsonSerializer.Serialize(arguments));
        return await SendAsync(request, TransferSide.Target, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DropboxReply> SendAsync(HttpRequestMessage request, TransferSide side, CancellationToken cancellationToken)
    {
        using var response = await Api.SendAsync(request, side, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var body = Parse(text);
        if (response.IsSuccessStatusCode)
        {
            return new DropboxReply(body, null, null);
        }

        // Dropbox explains errors with a summary such as "path/not_found/..", or plain text for a bad request
        var summary = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error_summary", out var value) ? value.GetString() ?? "" : text;
        return new DropboxReply(body, summary, Error(response, summary, side));
    }

    private static JsonElement Parse(string text)
    {
        if (text.Length == 0)
        {
            return default;
        }
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// The error Dropbox means with <paramref name="summary"/>. A 409 is an error of the endpoint, named by the
    /// summary; other statuses mean what they mean in HTTP.
    /// </summary>
    private static HttpTransferException Error(HttpResponseMessage response, string summary, TransferSide side)
    {
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            return summary.Contains("expired_access_token", StringComparison.Ordinal)
                ? HttpTransferException.FromResponse(response, "The Dropbox token expired", TransferErrorKind.Unauthorized, isTransient: false, side)
                : HttpTransferException.FromResponse(response, side);
        }

        var (kind, transient) = summary switch
        {
            _ when summary.Contains("not_found", StringComparison.Ordinal) => (TransferErrorKind.NotFound, false),
            _ when summary.Contains("insufficient_space", StringComparison.Ordinal) => (TransferErrorKind.InsufficientStorage, false),
            _ when summary.Contains("conflict", StringComparison.Ordinal) => (TransferErrorKind.AlreadyExists, false),
            _ when summary.Contains("too_many_write_operations", StringComparison.Ordinal) => (TransferErrorKind.RateLimited, true),
            // An upload session that was closed or timed out takes no more chunks; a new one does
            _ when summary.Contains("closed", StringComparison.Ordinal) => (TransferErrorKind.Expired, false),
            _ when summary.Contains("disallowed_name", StringComparison.Ordinal) || summary.Contains("malformed_path", StringComparison.Ordinal)
                => (TransferErrorKind.Unsupported, false),
            _ => (TransferErrorKind.Protocol, false)
        };
        return HttpTransferException.FromResponse(response, $"Dropbox: {summary}", kind, transient, side);
    }
}

/// <summary>
/// Dropbox's answer to a call: its JSON, and the error summary and error when it failed.
/// </summary>
internal sealed record DropboxReply(JsonElement Body, string? ErrorSummary, TransferException? Error);
