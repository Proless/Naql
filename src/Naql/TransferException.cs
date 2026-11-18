using System;
using System.Collections.Generic;

namespace Naql;

/// <summary>
/// What went wrong in a transfer.
/// </summary>
public enum TransferErrorKind
{
    /// <summary>The error doesn't fit another kind.</summary>
    Unknown,

    /// <summary>The content or its location doesn't exist.</summary>
    NotFound,

    /// <summary>The credentials are missing, wrong, or don't allow the operation.</summary>
    AccessDenied,

    /// <summary>The connection failed or broke off.</summary>
    Network,

    /// <summary>A response didn't arrive, or the transfer stalled, within the configured timeout.</summary>
    Timeout,

    /// <summary>The other side answered in a way the protocol doesn't allow or expect.</summary>
    Protocol,

    /// <summary>The content changed while it was being transferred.</summary>
    SourceChanged,

    /// <summary>The transferred bytes don't match what was expected, for example a wrong length.</summary>
    Integrity,

    /// <summary>The target has no room for the content.</summary>
    InsufficientStorage,

    /// <summary>The target already exists and may not be replaced.</summary>
    AlreadyExists,

    /// <summary>The source and target can't do what the transfer needs.</summary>
    Unsupported
}

/// <summary>
/// Which side of a transfer an error came from.
/// </summary>
public enum TransferSide
{
    /// <summary>Not known.</summary>
    Unknown,

    /// <summary>The <see cref="ITransferSource"/>.</summary>
    Source,

    /// <summary>The <see cref="ITransferTarget"/>.</summary>
    Target,

    /// <summary>The engine itself, for example a failed length check.</summary>
    Engine
}


/// <summary>
/// Something in a transfer went wrong. Sources and targets throw it to tell the engine what went wrong and whether
/// retrying can help; protocol packages derive from it to add their own details, such as an HTTP status code.
/// </summary>
/// <remarks>
/// An exception is never changed after it's thrown. When a transfer fails, <see cref="TransferOperation.RunAsync"/>
/// throws a <see cref="TransferFailedException"/> that wraps the failure.
/// </remarks>
public class TransferException : Exception
{
    /// <summary>
    /// Creates an exception.
    /// </summary>
    /// <param name="message">What went wrong</param>
    /// <param name="kind">What kind of error it is</param>
    /// <param name="isTransient">Whether trying again can succeed</param>
    /// <param name="side">Which side the error came from, when the thrower knows</param>
    /// <param name="retryDelay">How long the other side asked to wait before trying again</param>
    /// <param name="innerException">The error that caused this one</param>
    public TransferException(string message, TransferErrorKind kind, bool isTransient = false, TransferSide side = TransferSide.Unknown,
        TimeSpan? retryDelay = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        IsTransient = isTransient;
        Side = side;
        RetryDelay = retryDelay;
    }

    /// <summary>
    /// What kind of error it is.
    /// </summary>
    public TransferErrorKind Kind { get; }

    /// <summary>
    /// Whether trying again can succeed. The engine only retries transient errors.
    /// </summary>
    public bool IsTransient { get; }

    /// <summary>
    /// Which side the error came from, or <see cref="TransferSide.Unknown"/> when the thrower didn't say.
    /// </summary>
    public TransferSide Side { get; }

    /// <summary>
    /// How long the other side asked to wait before trying again, such as HTTP's <c>Retry-After</c>. The engine waits at
    /// least this long before its next attempt.
    /// </summary>
    public TimeSpan? RetryDelay { get; }
}

/// <summary>
/// A transfer failed. <see cref="TransferOperation.RunAsync"/> throws it for every failure, so it's the one type to
/// catch; <see cref="Error"/> holds the failure itself, for example a protocol's own exception type.
/// </summary>
/// <remarks>
/// <see cref="TransferException.Kind"/>, <see cref="TransferException.IsTransient"/> and
/// <see cref="TransferException.RetryDelay"/> are those of <see cref="Error"/>. <see cref="TransferException.Side"/> is
/// the side the engine saw the failure on.
/// </remarks>
public sealed class TransferFailedException : TransferException
{
    private static readonly IReadOnlyList<PartFailure> NoParts = [];

    /// <summary>
    /// Creates an exception for a failed transfer.
    /// </summary>
    /// <param name="error">The failure that ended the transfer</param>
    /// <param name="side">The side the failure came from</param>
    /// <param name="partFailures">Every part that failed, in the order they failed</param>
    public TransferFailedException(TransferException error, TransferSide side, IReadOnlyList<PartFailure>? partFailures = null)
        : base((error ?? throw new ArgumentNullException(nameof(error))).Message, error.Kind, error.IsTransient, side, error.RetryDelay, error)
    {
        Error = error;
        PartFailures = partFailures ?? NoParts;
    }

    /// <summary>
    /// The failure that ended the transfer. It's also the <see cref="Exception.InnerException"/>.
    /// </summary>
    public TransferException Error { get; }

    /// <summary>
    /// Every part that failed, in the order they failed; empty when the failure wasn't in a part, such as opening the
    /// target. The first one is usually <see cref="Error"/>; the others failed in parallel or were stopped by it.
    /// </summary>
    public IReadOnlyList<PartFailure> PartFailures { get; }
}

/// <summary>
/// One part of a transfer that failed.
/// </summary>
public sealed class PartFailure
{
    /// <summary>
    /// Creates a part failure.
    /// </summary>
    public PartFailure(ByteRange part, TransferException error, TransferSide side, int attempts)
    {
        Part = part;
        Error = error ?? throw new ArgumentNullException(nameof(error));
        Side = side;
        Attempts = attempts;
    }

    /// <summary>
    /// The bytes of the part.
    /// </summary>
    public ByteRange Part { get; }

    /// <summary>
    /// What the last attempt failed with.
    /// </summary>
    public TransferException Error { get; }

    /// <summary>
    /// The side the engine saw the failure on.
    /// </summary>
    public TransferSide Side { get; }

    /// <summary>
    /// How many attempts the part had, the first one included.
    /// </summary>
    public int Attempts { get; }
}
