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
/// A transfer failed. Sources and targets throw it to tell the engine what went wrong and whether retrying can help.
/// </summary>
public class TransferException : Exception
{
    private static readonly IReadOnlyList<TransferException> NoFailures = [];

    private TransferSide _side;
    private ByteRange? _part;

    /// <summary>
    /// Creates an exception.
    /// </summary>
    /// <param name="message">What went wrong</param>
    /// <param name="kind">What kind of error it is</param>
    /// <param name="isTransient">Whether trying again can succeed</param>
    /// <param name="innerException">The error that caused this one</param>
    public TransferException(string message, TransferErrorKind kind = TransferErrorKind.Unknown, bool isTransient = false, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        IsTransient = isTransient;
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
    /// Which side the error came from. The engine fills it in when the thrower didn't.
    /// </summary>
    public TransferSide Side
    {
        get => _side;
        init => _side = value;
    }

    /// <summary>
    /// The part that failed, or <c>null</c> when the error isn't about one part. The engine fills it in when the thrower didn't.
    /// </summary>
    public ByteRange? Part
    {
        get => _part;
        init => _part = value;
    }

    /// <summary>
    /// How long the other side asked to wait before trying again, such as HTTP's <c>Retry-After</c>.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// On the exception a transfer ends with: every part failure, in the order they happened. Empty otherwise.
    /// </summary>
    public IReadOnlyList<TransferException> PartFailures { get; private set; } = NoFailures;

    /// <summary>
    /// Fills in the side and part when the thrower didn't set them.
    /// </summary>
    internal TransferException WithContext(TransferSide side, ByteRange? part)
    {
        if (_side == TransferSide.Unknown)
        {
            _side = side;
        }
        _part ??= part;
        return this;
    }

    internal void SetPartFailures(IReadOnlyList<TransferException> failures)
    {
        PartFailures = failures;
    }
}
