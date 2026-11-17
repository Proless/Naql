using System;

namespace Naql;

/// <summary>
/// The outcome of a successful transfer.
/// </summary>
public sealed class TransferResult
{
    internal TransferResult(SourceInfo source, long transferred, long resumed, TimeSpan duration, int parts, int retries, Checksum? verifiedChecksum)
    {
        Source = source;
        Transferred = transferred;
        Resumed = resumed;
        Duration = duration;
        Parts = parts;
        Retries = retries;
        VerifiedChecksum = verifiedChecksum;
    }

    /// <summary>
    /// What the source reported about the content.
    /// </summary>
    public SourceInfo Source { get; }

    /// <summary>
    /// The number of bytes the target holds, including those an earlier run had written.
    /// </summary>
    public long Transferred { get; }

    /// <summary>
    /// The bytes an earlier run had written, which this run continued from a <see cref="TransferCheckpoint"/>.
    /// </summary>
    public long Resumed { get; }

    /// <summary>
    /// How long this run took.
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// Bytes per second this run moved.
    /// </summary>
    public double AverageRate => Duration > TimeSpan.Zero ? (Transferred - Resumed) / Duration.TotalSeconds : 0;

    /// <summary>
    /// How many parts the content was split into.
    /// </summary>
    public int Parts { get; }

    /// <summary>
    /// How many times an operation was retried after a transient error.
    /// </summary>
    public int Retries { get; }

    /// <summary>
    /// The checksum the content was verified against, or <c>null</c> when it wasn't verified.
    /// </summary>
    public Checksum? VerifiedChecksum { get; }
}
