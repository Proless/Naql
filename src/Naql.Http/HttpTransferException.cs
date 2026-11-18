using System;
using System.Net;
using System.Net.Http;

namespace Naql.Http;

/// <summary>
/// An HTTP request in a transfer failed. It carries what HTTP knows about the failure, such as the status code.
/// </summary>
public sealed class HttpTransferException : TransferException
{
    /// <summary>
    /// Creates an exception.
    /// </summary>
    /// <param name="message">What went wrong</param>
    /// <param name="kind">What kind of error it is</param>
    /// <param name="isTransient">Whether trying again can succeed</param>
    /// <param name="side">Which side of the transfer made the request</param>
    /// <param name="method">The request method</param>
    /// <param name="requestUri">The requested URL</param>
    /// <param name="statusCode">The response status, or <c>null</c> when no response arrived</param>
    /// <param name="reasonPhrase">The response's reason phrase</param>
    /// <param name="retryDelay">How long the server asked to wait, from <c>Retry-After</c></param>
    /// <param name="innerException">The error that caused this one</param>
    public HttpTransferException(string message, TransferErrorKind kind, bool isTransient, TransferSide side, HttpMethod method, Uri requestUri,
        HttpStatusCode? statusCode = null, string? reasonPhrase = null, TimeSpan? retryDelay = null, Exception? innerException = null)
        : base(message, kind, isTransient, side, retryDelay, innerException)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        RequestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
    }

    /// <summary>
    /// The request method.
    /// </summary>
    public HttpMethod Method { get; }

    /// <summary>
    /// The requested URL.
    /// </summary>
    public Uri RequestUri { get; }

    /// <summary>
    /// The response status, or <c>null</c> when no response arrived, for example after a DNS failure or a reset connection.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// The response's reason phrase, such as <c>Not Found</c>, when there was a response.
    /// </summary>
    public string? ReasonPhrase { get; }
}
