using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Naql;

/// <summary>
/// Where a transfer writes its content to: a file, an HTTP upload, a stream.
/// </summary>
public interface ITransferTarget
{
    /// <summary>
    /// What the target supports. The engine plans the transfer around it.
    /// </summary>
    TargetCapabilities Capabilities { get; }

    /// <summary>
    /// Starts writing a new transfer, or continues an unfinished one when <see cref="TargetOpenRequest.ResumeToken"/> is set.
    /// </summary>
    Task<ITargetSession> OpenAsync(TargetOpenRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// What <see cref="ITransferTarget.OpenAsync"/> should prepare for.
/// </summary>
public sealed class TargetOpenRequest
{
    /// <summary>
    /// Creates a request.
    /// </summary>
    public TargetOpenRequest(SourceInfo source, string? resumeToken = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ResumeToken = resumeToken;
    }

    /// <summary>
    /// The content that will be written: its length, name, media type and metadata.
    /// </summary>
    public SourceInfo Source { get; }

    /// <summary>
    /// Identifies an unfinished transfer to continue, from <see cref="ITargetSession.ResumeToken"/>.
    /// </summary>
    public string? ResumeToken { get; }
}

/// <summary>
/// One transfer being written to a target.
/// </summary>
/// <remarks>
/// Disposing the session without calling <see cref="CompleteAsync"/> or <see cref="AbortAsync"/> must keep what was
/// written, so a resumable transfer can continue later.
/// </remarks>
public interface ITargetSession : IDisposable
{
    /// <summary>
    /// Identifies this unfinished transfer so it can be continued later (a temporary file, an upload id), or <c>null</c>.
    /// </summary>
    string? ResumeToken { get; }

    /// <summary>
    /// Writes one part of the content.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Read <paramref name="content"/> to its end. It holds exactly <c>range.Length</c> bytes when the length is known.</item>
    /// <item>The engine may write the same range again after a failure; the new bytes replace the old ones.</item>
    /// <item>With <see cref="TargetCapabilities.ResumableParts"/>, the engine may continue a failed part with the rest of
    /// its range. Every byte read from <paramref name="content"/> before reading it failed must then be kept.</item>
    /// <item>With <see cref="TargetCapabilities.ParallelParts"/>, several parts are written at the same time.</item>
    /// <item>Honor <paramref name="cancellationToken"/>; the engine uses it to stop stalled writes.</item>
    /// </list>
    /// </remarks>
    Task WritePartAsync(ByteRange range, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the result visible, for example by renaming a temporary file or completing a multipart upload.
    /// </summary>
    Task CompleteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Discards everything written so far.
    /// </summary>
    Task AbortAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A session that can read back what it wrote, so the engine can verify a checksum of content that arrived in parallel
/// parts or was resumed. Sessions of targets with <see cref="TargetCapabilities.Verifiable"/> implement it.
/// </summary>
public interface IVerifiableSession
{
    /// <summary>
    /// Hashes the first <paramref name="length"/> bytes of the content written so far, before it's completed.
    /// </summary>
    Task<byte[]> ComputeHashAsync(HashAlgorithmName algorithm, long length, CancellationToken cancellationToken);
}

/// <summary>
/// What a target supports.
/// </summary>
public sealed record TargetCapabilities
{
    /// <summary>
    /// Parts can be written at the same time, in any order.
    /// </summary>
    public bool ParallelParts { get; init; }

    /// <summary>
    /// The bytes a failed part read from its content are kept, so the part can continue where it stopped.
    /// </summary>
    public bool ResumableParts { get; init; }

    /// <summary>
    /// <see cref="ITransferTarget.OpenAsync"/> accepts a <see cref="TargetOpenRequest.ResumeToken"/>, and a session that is
    /// disposed without completing keeps what was written.
    /// </summary>
    public bool Resumable { get; init; }

    /// <summary>
    /// Writing a part again is harmless, so a write that failed after the target had the whole part can be retried.
    /// True for files and HTTP <c>PUT</c>; false for HTTP <c>POST</c>, where a repeated request can create a second
    /// upload. When false, a failed write is retried only if the target hadn't received the whole part yet.
    /// </summary>
    public bool RepeatableWrites { get; init; } = true;

    /// <summary>
    /// The length of the content must be known before writing starts, for example for a single HTTP PUT.
    /// </summary>
    public bool RequiresLength { get; init; }

    /// <summary>
    /// Sessions implement <see cref="IVerifiableSession"/>, so a checksum can be verified however the content arrived.
    /// Without it, a required checksum makes the engine use one connection and start over instead of resuming, so it can
    /// hash the bytes as they pass.
    /// </summary>
    public bool Verifiable { get; init; }

    /// <summary>
    /// The smallest part the target accepts, except for the last one. 0 when there is no minimum.
    /// </summary>
    public long MinPartSize
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(MinPartSize));
            field = value;
        }
    }

    /// <summary>
    /// The largest part the target accepts, or <c>null</c> when there is no maximum.
    /// </summary>
    public long? MaxPartSize
    {
        get;
        init
        {
            if (value is long size)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size, nameof(MaxPartSize));
            }
            field = value;
        }
    }

    /// <summary>
    /// The most parts the target accepts, or <c>null</c> when there is no maximum.
    /// </summary>
    public int? MaxParts
    {
        get;
        init
        {
            if (value is int parts)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parts, nameof(MaxParts));
            }
            field = value;
        }
    }
}
