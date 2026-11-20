using System;
using System.Net.Http;

namespace Naql.Http;

/// <summary>
/// Turns unsuccessful HTTP responses into transfer errors that say whether retrying can help.
/// </summary>
internal static class HttpErrors
{
    public static HttpTransferException FromStatus(HttpResponseMessage response, TransferSide side)
    {
        var code = (int)response.StatusCode;
        var (kind, transient) = code switch
        {
            404 or 410 => (TransferErrorKind.NotFound, false),
            401 or 407 => (TransferErrorKind.Unauthorized, false),
            403 => (TransferErrorKind.AccessDenied, false),
            408 => (TransferErrorKind.Timeout, true),
            413 or 507 => (TransferErrorKind.InsufficientStorage, false),
            429 => (TransferErrorKind.RateLimited, true),
            501 or 505 => (TransferErrorKind.Protocol, false),
            >= 500 => (TransferErrorKind.Network, true),
            _ => (TransferErrorKind.Protocol, false)
        };

        return FromResponse(response, $"The server answered {code} {response.ReasonPhrase}".TrimEnd(), kind, transient, side);
    }

    /// <summary>
    /// An error about a response, with its status, reason and <c>Retry-After</c>.
    /// </summary>
    public static HttpTransferException FromResponse(HttpResponseMessage response, string message, TransferErrorKind kind, bool transient, TransferSide side)
    {
        var request = response.RequestMessage;
        var retryAfter = response.Headers.RetryAfter;
        return new HttpTransferException(message, kind, transient, side,
            request?.Method ?? HttpMethod.Get,
            request?.RequestUri ?? throw new ArgumentException("The response has no request", nameof(response)),
            response.StatusCode,
            response.ReasonPhrase,
            retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow));
    }
}
