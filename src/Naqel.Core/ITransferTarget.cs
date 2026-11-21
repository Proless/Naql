using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Naqel;

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
    /// The buffer size targets copy with when no other is asked for, the same as <see cref="TransferOptions.BufferSize"/>'s default.
    /// </summary>
    public const int DefaultBufferSize = 81920;

    /// <summary>
    /// Creates a request.
    /// </summary>
    /// <param name="source">The content that will be written</param>
    /// <param name="resumeToken">The unfinished transfer to continue, or <c>null</c></param>
    /// <param name="bufferSize">The buffer size to copy with</param>
    public TargetOpenRequest(SourceInfo source, string? resumeToken = null, int bufferSize = DefaultBufferSize)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ResumeToken = resumeToken;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        BufferSize = bufferSize;
    }

    /// <summary>
    /// The content that will be written: its length, name, media type and metadata.
    /// </summary>
    public SourceInfo Source { get; }

    /// <summary>
    /// Identifies an unfinished transfer to continue, from <see cref="ITargetSession.ResumeToken"/>.
    /// </summary>
    public string? ResumeToken { get; }

    /// <summary>
    /// The buffer size to copy the content with, from <see cref="TransferOptions.BufferSize"/>. The engine hands over
    /// at most this many bytes per read, so a bigger buffer wouldn't fill.
    /// </summary>
    public int BufferSize { get; }
}

/// <summary>
/// One transfer being written to a target.
/// </summary>
/// <remarks>
/// Disposing the session without calling <see cref="CompleteAsync"/> or <see cref="AbortAsync"/> must keep what was
/// written, so a resumable transfer can continue later. Disposing is asynchronous because releasing a target can
/// involve I/O, such as closing a remote file.
/// </remarks>
public interface ITargetSession : IAsyncDisposable
{
    /// <summary>
    /// Where the content is once the session completed: a file's full path, an upload's URL, or <c>null</c> when there
    /// is nothing to point at, such as a stream. The engine reads it after <see cref="CompleteAsync"/> and returns it as
    /// <see cref="TransferResult.Location"/>.
    /// </summary>
    string? Location { get; }

    /// <summary>
    /// What the target created, once the session completed, such as a file id, a revision or a share link; empty when
    /// there is nothing to tell. The engine reads it after <see cref="CompleteAsync"/> and returns it as
    /// <see cref="TransferResult.TargetMetadata"/>. Keys are up to the target; a package documents the ones it sets.
    /// </summary>
    IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>
    /// Identifies this unfinished transfer so it can be continued later (a temporary file, an upload id), or <c>null</c>
    /// when this session can't be continued, even if the target is <see cref="TargetCapabilities.Resumable"/>.
    /// </summary>
    string? ResumeToken { get; }

    /// <summary>
    /// Writes one part of the content.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Read <paramref name="content"/> to its end. It holds exactly <c>range.Length</c> bytes when the length is known,
    /// or fewer with <see cref="TargetCapabilities.SplittableParts"/>, when the engine gave the end of the range to another
    /// part while this one ran.</item>
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
/// A session that keeps only what its other side acknowledged, such as an upload session whose server stores a chunk
/// once it confirmed it, so bytes it read from a part aren't kept until then.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The engine asks it how much was kept instead of counting what the session read: a failed part continues from
/// there, even when the session itself failed, and checkpoints count only acknowledged bytes. The target declares
/// <see cref="TargetCapabilities.ResumableParts"/> for that.</item>
/// <item>A session opened with a <see cref="TargetOpenRequest.ResumeToken"/> asks its server where it stands before
/// <see cref="ITransferTarget.OpenAsync"/> returns, so parts continue from what the server really has, not from what a
/// possibly older checkpoint says.</item>
/// <item>A <see cref="ITargetSession.WritePartAsync"/> that returns has had its whole part acknowledged.</item>
/// </list>
/// </remarks>
public interface IAcknowledgingSession
{
    /// <summary>
    /// How many bytes from <paramref name="position"/> of the content on the session has kept for good, without a gap;
    /// 0 when nothing there is acknowledged yet.
    /// </summary>
    /// <remarks>
    /// The engine calls it while writes run and from other threads, for checkpoints, and once more after the session is
    /// disposed, for the last checkpoint. It must be safe to call at any of those times and answer at once, from what
    /// the session already knows, without asking the server.
    /// </remarks>
    long Kept(long position);
}

/// <summary>
/// A session that can read back what it wrote, so the engine can verify a checksum of content that arrived in parallel
/// parts or was resumed. Sessions of targets with <see cref="TargetCapabilities.Verifiable"/> must implement it; the
/// engine fails the transfer as <see cref="TransferErrorKind.Unsupported"/> when one doesn't.
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
    /// A part may end before the end of the range its write was given: the engine then gave the rest of the range to a
    /// new part, so a connection that freed up can move it, and the content stream simply ends sooner. True when a
    /// session writes each byte where it belongs as it arrives, such as a file; false when it needs a part's size up
    /// front, such as a multipart upload that declares each part's size. Needs <see cref="ParallelParts"/>.
    /// </summary>
    public bool SplittableParts { get; init; }

    /// <summary>
    /// A failed part can continue where it stopped: every byte the session read from the part is kept, or, for a session
    /// that implements <see cref="IAcknowledgingSession"/>, every byte it acknowledged.
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

    /// <summary>
    /// Every part but the last must be a multiple of this many bytes, such as 320 KiB for OneDrive's upload
    /// sessions, or <c>null</c> when any size will do. The planner rounds part sizes to it.
    /// </summary>
    public long? PartSizeMultiple
    {
        get;
        init
        {
            if (value is long multiple)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(multiple, nameof(PartSizeMultiple));
            }
            field = value;
        }
    }
}
