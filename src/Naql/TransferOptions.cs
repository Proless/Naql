using System;
using System.Threading;

namespace Naql;

/// <summary>
/// Settings for one transfer. They can't change once the transfer is built; use <c>with</c> to derive new options.
/// </summary>
public sealed record TransferOptions
{
    /// <summary>
    /// The default settings.
    /// </summary>
    public static TransferOptions Default { get; } = new();

    /// <summary>
    /// How many parts can be transferred at the same time. Parts are only used when the source supports ranges and the
    /// target supports parallel parts; otherwise the transfer uses one connection.
    /// </summary>
    public int MaxConnections
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(MaxConnections));
            field = value;
        }
    } = 1;

    /// <summary>
    /// The highest average rate for the whole transfer, across all its parts. 0 means unlimited.
    /// </summary>
    public long MaxBytesPerSecond
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(MaxBytesPerSecond));
            field = value;
        }
    }

    /// <summary>
    /// How transient errors are retried. Each part retries on its own.
    /// </summary>
    public RetryPolicy Retry
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value, nameof(Retry));
            field = value;
        }
    } = RetryPolicy.Default;

    /// <summary>
    /// How long to wait for a source or target to respond: for the source to describe or start sending the content,
    /// and for the target to open, to finish a part once it has read all of it (such as the server's answer to an
    /// upload), to complete, and to clean up after a failure. Reading the target back to verify a checksum has no limit,
    /// since it takes as long as the content is large.
    /// </summary>
    public TimeSpan ResponseTimeout
    {
        get;
        init => field = ValidTimeout(value, nameof(ResponseTimeout));
    } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a part can go without moving any bytes before it fails as stalled. Time spent waiting for the speed
    /// limit doesn't count, and neither does the time after the target has read the whole part, which
    /// <see cref="ResponseTimeout"/> covers.
    /// </summary>
    public TimeSpan IdleTimeout
    {
        get;
        init => field = ValidTimeout(value, nameof(IdleTimeout));
    } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The most bytes read from the source at once, and the buffer size targets copy with
    /// (<see cref="TargetOpenRequest.BufferSize"/>).
    /// </summary>
    public int BufferSize
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(BufferSize));
            field = value;
        }
    } = 81920;

    /// <summary>
    /// The smallest part worth its own connection when the engine splits the content: content smaller than twice this
    /// size uses one connection. It's a preference, unlike <see cref="TargetCapabilities.MinPartSize"/>, which is a limit:
    /// a target's <see cref="TargetCapabilities.MaxPartSize"/> can still make parts smaller than this. With
    /// <see cref="AdaptiveSplitting"/>, a running part is only split for a connection that freed up when at least twice
    /// this size is left of it.
    /// </summary>
    public long MinSplitPartSize
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(MinSplitPartSize));
            field = value;
        }
    } = 1024 * 1024;

    /// <summary>
    /// How often progress is reported.
    /// </summary>
    public TimeSpan ProgressInterval
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(ProgressInterval));
            field = value;
        }
    } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How often the checkpoint is saved while the transfer runs, when it has a <see cref="ICheckpointStore"/>. It's
    /// saved once more when the transfer stops, either way.
    /// </summary>
    public TimeSpan CheckpointInterval
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(CheckpointInterval));
            field = value;
        }
    } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Discard what was written when the transfer fails or is canceled, even when the target could resume it.
    /// </summary>
    public bool DiscardPartialOnFailure { get; init; }

    /// <summary>
    /// When a connection frees up and no part waits for it, give the end of the running part with the most left to a
    /// new part on that connection, so a slow part doesn't hold up the end of the transfer, and more connections set
    /// while it runs are used even when every planned part already started. On by default; it only splits for targets
    /// with <see cref="TargetCapabilities.SplittableParts"/>. Turn it off to keep exactly the planned parts.
    /// </summary>
    public bool AdaptiveSplitting { get; init; } = true;

    /// <summary>
    /// The hash the content must have. The transfer fails with <see cref="TransferErrorKind.Integrity"/> and discards
    /// what it wrote when it doesn't match, before the target makes the result visible.
    /// </summary>
    /// <remarks>
    /// One connection that wasn't resumed is hashed as the bytes pass. Parallel parts and resumed transfers are hashed by
    /// reading the target back, which needs a <see cref="TargetCapabilities.Verifiable"/> target; for other targets the
    /// transfer uses one connection and starts over instead of resuming. A checksum reported by the source
    /// (<see cref="SourceInfo.Checksum"/>) is verified only when that costs nothing extra.
    /// </remarks>
    public Checksum? ExpectedChecksum { get; init; }

    /// <summary>
    /// A timeout must be positive or <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </summary>
    private static TimeSpan ValidTimeout(TimeSpan value, string name)
    {
        return value > TimeSpan.Zero || value == Timeout.InfiniteTimeSpan
            ? value
            : throw new ArgumentOutOfRangeException(name, value, "The timeout must be positive or Timeout.InfiniteTimeSpan");
    }
}
