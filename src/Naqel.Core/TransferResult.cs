using System;
using System.Collections.Generic;

namespace Naqel;

/// <summary>
/// The outcome of a successful transfer.
/// </summary>
public sealed class TransferResult
{
    internal TransferResult(SourceInfo source, long transferred, long resumed, TimeSpan duration, int parts, int retries, Checksum? verifiedChecksum,
        string? location, IReadOnlyDictionary<string, string> targetMetadata, SingleConnectionReason singleConnectionReason)
    {
        TargetMetadata = targetMetadata;
        SingleConnectionReason = singleConnectionReason;
        Source = source;
        Location = location;
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
    /// Where the content is now: a file's full path, an upload's URL, or <c>null</c> when the target has nothing to
    /// point at, such as a stream. It comes from <see cref="ITargetSession.Location"/>.
    /// </summary>
    public string? Location { get; }

    /// <summary>
    /// What the target created, such as a file id or a share link, from <see cref="ITargetSession.Metadata"/>; empty when
    /// it has nothing to tell.
    /// </summary>
    public IReadOnlyDictionary<string, string> TargetMetadata { get; }

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
    /// Why the transfer used one connection, or <see cref="Naqel.SingleConnectionReason.None"/> when it used more.
    /// </summary>
    public SingleConnectionReason SingleConnectionReason { get; }

    /// <summary>
    /// How many times an operation was retried after a transient error.
    /// </summary>
    public int Retries { get; }

    /// <summary>
    /// The checksum the content was verified against, or <c>null</c> when it wasn't verified.
    /// </summary>
    public Checksum? VerifiedChecksum { get; }
}

/// <summary>
/// Why a transfer moved its content in one part, over one connection.
/// </summary>
public enum SingleConnectionReason
{
    /// <summary>The transfer used more than one part.</summary>
    None,

    /// <summary><see cref="TransferOptions.MaxConnections"/> is 1.</summary>
    NotRequested,

    /// <summary>The source didn't report the length, so the content can't be split.</summary>
    UnknownLength,

    /// <summary>The source can't read ranges.</summary>
    NoRangeSupport,

    /// <summary>
    /// The source reported no version, so separate reads could come from different versions, and no checksum would
    /// catch that.
    /// </summary>
    NoVersion,

    /// <summary>
    /// A required checksum is hashed as the bytes pass, because the target can't read them back, and that needs one
    /// connection from the first byte.
    /// </summary>
    ChecksumHashedWhileStreaming,

    /// <summary>The target takes the content in one part, such as one HTTP upload.</summary>
    TargetTakesOnePart,

    /// <summary>The content is smaller than two parts of <see cref="TransferOptions.MinSplitPartSize"/>.</summary>
    TooSmall,

    /// <summary>The transfer continued a checkpoint that had one part.</summary>
    ContinuedCheckpoint,

    /// <summary>The source allows one connection at a time, see <see cref="SourceInfo.MaxConnections"/>.</summary>
    SourceLimit
}
