using System;
using System.Net.Http;

namespace Naql.Http;

/// <summary>
/// Turns unsuccessful HTTP responses into transfer errors that say whether retrying can help.
/// </summary>
internal static class HttpErrors
{
    public static TransferException FromStatus(HttpResponseMessage response, TransferSide side)
    {
        var code = (int)response.StatusCode;
        var (kind, transient) = code switch
        {
            404 or 410 => (TransferErrorKind.NotFound, false),
            401 or 403 or 407 => (TransferErrorKind.AccessDenied, false),
            408 => (TransferErrorKind.Timeout, true),
            413 or 507 => (TransferErrorKind.InsufficientStorage, false),
            429 => (TransferErrorKind.Network, true),
            501 or 505 => (TransferErrorKind.Protocol, false),
            >= 500 => (TransferErrorKind.Network, true),
            _ => (TransferErrorKind.Protocol, false)
        };

        var retryAfter = response.Headers.RetryAfter;
        return new TransferException($"The server answered {code} {response.ReasonPhrase}".TrimEnd(), kind, transient)
        {
            Side = side,
            RetryAfter = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow)
        };
    }
}
