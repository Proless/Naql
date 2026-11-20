namespace Naql.Engine;

/// <summary>
/// A part failed in a way it could only recover from with a new target session, such as having to send again bytes
/// an acknowledging session already acknowledged. The run starts over with a new session, once; if it already did,
/// the transfer fails with the error that caused it, which this exception carries as its own.
/// </summary>
internal sealed class StartOverException(TransferException cause)
    : TransferException(cause.Message, cause.Kind, cause.IsTransient, cause.Side, cause.RetryDelay, cause);
