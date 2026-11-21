using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Http;

/// <summary>
/// Authenticates requests with an OAuth bearer token, and gets a fresh one when the server rejects it, for services
/// such as Google Drive, OneDrive and Dropbox. Put it in front of the <see cref="HttpClient"/> an
/// <see cref="HttpTransport"/> uses.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Getting the first token, such as by signing in, is the caller's; the handler only asks the token function
/// for the current token, or for a fresh one after a rejection.</item>
/// <item>A request that's answered with 401 is sent again with a fresh token, once, when its body can be sent twice:
/// it has none, or one in memory such as a <see cref="StringContent"/> or another <see cref="ByteArrayContent"/>,
/// like the JSON of an API call.</item>
/// <item>A streamed body, such as an upload's, can't be sent twice. The handler gets a fresh token and fails the
/// attempt as a transient <see cref="TransferErrorKind.Unauthorized"/>, so the transfer's retry sends it again with
/// the fresh token.</item>
/// <item>Requests rejected at the same time share one refresh: a request whose token was already replaced uses the
/// new one.</item>
/// </list>
/// </remarks>
[SuppressMessage("Design", "CA1001", Justification = "The lock never creates a wait handle, so it holds nothing to release")]
public sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly Func<bool, CancellationToken, Task<string>> _getToken;
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    /// <summary>
    /// A handler that gets its tokens from <paramref name="getToken"/>.
    /// </summary>
    /// <param name="getToken">Returns the current token, or with <c>true</c> a fresh one because the current one was
    /// rejected; after a refresh, it returns the fresh token as the current one</param>
    public BearerTokenHandler(Func<bool, CancellationToken, Task<string>> getToken)
    {
        _getToken = getToken ?? throw new ArgumentNullException(nameof(getToken));
    }

    /// <summary>
    /// A handler that gets its tokens from <paramref name="getToken"/> and sends requests through <paramref name="innerHandler"/>.
    /// </summary>
    /// <param name="getToken">Returns the current token, or with <c>true</c> a fresh one because the current one was
    /// rejected; after a refresh, it returns the fresh token as the current one</param>
    /// <param name="innerHandler">Sends the requests</param>
    public BearerTokenHandler(Func<bool, CancellationToken, Task<string>> getToken, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _getToken = getToken ?? throw new ArgumentNullException(nameof(getToken));
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = await _getToken(false, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var fresh = await RefreshAsync(token, cancellationToken).ConfigureAwait(false);
        if (request.Content is not (null or ByteArrayContent))
        {
            var error = HttpTransferException.FromResponse(response, "The access token was rejected; the next attempt uses a fresh one",
                TransferErrorKind.Unauthorized, isTransient: true, TransferSide.Unknown);
            response.Dispose();
            throw error;
        }

        response.Dispose();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A token to use instead of <paramref name="rejected"/>: the current one, if another request replaced it already,
    /// or else a fresh one.
    /// </summary>
    private async Task<string> RefreshAsync(string rejected, CancellationToken cancellationToken)
    {
        await _refreshing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _getToken(false, cancellationToken).ConfigureAwait(false);
            return current != rejected ? current : await _getToken(true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshing.Release();
        }
    }
}
