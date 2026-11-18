using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace Naql.Engine;

/// <summary>
/// What a run knows and has done so far: the content, its parts and the counters behind progress and the result. It
/// also decides what a checkpoint holds and whether one can be continued, and keeps the hash of a part that is checked
/// as it passes. Progress and checkpoints read it from other threads while the run changes it.
/// </summary>
internal sealed class RunState
{
    private readonly TransferOptions _options;
    private readonly TargetCapabilities _capabilities;
    private readonly RateMeter _rate = new();

    private volatile SourceInfo? _info;
    private volatile TransferState _state;
    private volatile List<PartRun>? _parts;
    private volatile string? _resumeToken;
    private volatile bool _discarded;
    private volatile bool _completed;
    private long _transferred;
    private long _resumed;
    private int _retries;
    private int _retryingParts;
    private int _activeParts;
    private int _completedParts;
    private int _totalParts;
    private Exception? _lastError;
    private IncrementalHash? _hash;
    private bool _hashable;

    public RunState(TransferOptions options, TargetCapabilities capabilities, TagList tags)
    {
        _options = options;
        _capabilities = capabilities;
        Tags = tags;
    }

    /// <summary>
    /// The tags every metric of this run carries.
    /// </summary>
    public TagList Tags { get; }

    /// <summary>
    /// Measures the run, and the activity the idle timeout compares against.
    /// </summary>
    public Stopwatch Clock { get; } = new();

    /// <summary>
    /// The content, once the source described it.
    /// </summary>
    public SourceInfo? Info => _info;

    public TransferState State
    {
        get => _state;
        set => _state = value;
    }

    /// <summary>
    /// The target's token for its partial data, so checkpoints can find it again.
    /// </summary>
    public string? ResumeToken
    {
        get => _resumeToken;
        set => _resumeToken = value;
    }

    /// <summary>
    /// The target's partial data was thrown away, so no checkpoint can refer to it.
    /// </summary>
    public bool Discarded
    {
        get => _discarded;
        set => _discarded = value;
    }

    /// <summary>
    /// The target committed the content.
    /// </summary>
    public bool Completed
    {
        get => _completed;
        set => _completed = value;
    }

    public long Transferred => Interlocked.Read(ref _transferred);

    public long Resumed => Interlocked.Read(ref _resumed);

    public int Retries => Volatile.Read(ref _retries);

    public int TotalParts => Volatile.Read(ref _totalParts);

    /// <summary>
    /// The content's checksum, once it was verified.
    /// </summary>
    public Checksum? Verified { get; set; }

    /// <summary>
    /// Accepts the description of the content, if the target can work with it.
    /// </summary>
    public void SetInfo(SourceInfo? info)
    {
        if (info == null)
        {
            throw new TransferException("The source didn't describe the content", TransferErrorKind.Protocol, side: TransferSide.Source);
        }
        if (_capabilities.RequiresLength && info.Length == null)
        {
            throw new TransferException("The target needs to know the length up front, but the source didn't report one", TransferErrorKind.Unsupported, side: TransferSide.Engine);
        }
        _info = info;
    }

    /// <summary>
    /// Starts moving <paramref name="parts"/>. The hash is only kept for a single part that starts at its first byte.
    /// </summary>
    public void SetParts(List<PartRun> parts)
    {
        _hashable = parts.Count == 1 && parts[0].Consumed == 0;
        _parts = parts;
        Volatile.Write(ref _totalParts, parts.Count);
        Volatile.Write(ref _completedParts, parts.Count(part => part.Done));
    }

    /// <summary>
    /// Forgets the content and everything moved, to start over with content that changed.
    /// </summary>
    public void Restart()
    {
        _info = null;
        _parts = null;
        Interlocked.Exchange(ref _transferred, 0);
        Interlocked.Exchange(ref _resumed, 0);
        Interlocked.Exchange(ref _completedParts, 0);
        Interlocked.Increment(ref _retries);
        _rate.Reset();
    }

    public void AddTransferred(long bytes) => Interlocked.Add(ref _transferred, bytes);

    public void PartStarted() => Interlocked.Increment(ref _activeParts);

    public void PartStopped() => Interlocked.Decrement(ref _activeParts);

    public void PartCompleted() => Interlocked.Increment(ref _completedParts);

    /// <summary>
    /// Counts a retry and shows its error in progress until it ends.
    /// </summary>
    public void RetryStarted(TransferException error)
    {
        Interlocked.Increment(ref _retries);
        Volatile.Write(ref _lastError, error);
        Interlocked.Increment(ref _retryingParts);
    }

    public void RetryEnded() => Interlocked.Decrement(ref _retryingParts);

    /// <summary>
    /// Whether bytes from separate requests can be joined into one result: the source names a version every request
    /// is checked against, or a required checksum would catch content that changed in between.
    /// </summary>
    public bool CanCombine(SourceInfo info) => info.Version != null || _options.ExpectedChecksum != null;

    /// <summary>
    /// Whether a failed part can continue where it stopped. Continuing joins bytes from two requests, which is only safe
    /// when they are known to be the same content.
    /// </summary>
    public bool CanResumePart() => _capabilities.ResumableParts && _info is { SupportsRanges: true } info && CanCombine(info);

    /// <summary>
    /// What the target has kept so far, or <c>null</c> when the transfer can't be continued later: it finished, its
    /// partial data was discarded, the target can't resume, or the source has no version or ranges to check and
    /// continue with.
    /// </summary>
    public TransferCheckpoint? GetCheckpoint()
    {
        var info = _info;
        var parts = _parts;
        var token = _resumeToken;
        if (_completed || _discarded || info == null || parts == null || token == null ||
            info.Version == null || !info.SupportsRanges || !_capabilities.Resumable)
        {
            return null;
        }

        var resumableParts = _capabilities.ResumableParts;
        return new TransferCheckpoint(info.Length, TransferCheckpoint.Fingerprint(info.Identity), info.Version, token, parts.Select(part =>
        {
            var range = part.Range.IsAll ? new ByteRange(0, info.Length) : part.Range;
            var completed = part.Done && range.Length is long length ? length : resumableParts ? part.Confirmed : 0;
            return new CheckpointPart(range, Math.Min(completed, range.Length ?? completed));
        }));
    }

    /// <summary>
    /// Whether <paramref name="checkpoint"/> can continue a transfer of the content <paramref name="info"/> describes:
    /// the same content in the same version, a source that can read ranges, and a target that can resume.
    /// </summary>
    public bool CanContinue(TransferCheckpoint checkpoint, SourceInfo info)
    {
        return _capabilities.Resumable && checkpoint.ResumeToken != null && info.SupportsRanges &&
               info.Version != null && info.Version == checkpoint.SourceVersion && info.Length == checkpoint.Length &&
               TransferCheckpoint.Fingerprint(info.Identity) == checkpoint.SourceIdentity;
    }

    /// <summary>
    /// The parts of a checkpoint the target reopened, with what it kept counted as already moved.
    /// </summary>
    public List<PartRun> ContinueFrom(TransferCheckpoint checkpoint)
    {
        var parts = checkpoint.Parts.Select(saved =>
        {
            var kept = saved.IsComplete || _capabilities.ResumableParts ? saved.Completed : 0;
            return new PartRun(saved.Range, saved.Range.Length) { Consumed = kept, Confirmed = kept, Done = saved.IsComplete };
        }).ToList();

        var resumed = parts.Sum(part => part.Consumed);
        Interlocked.Exchange(ref _resumed, resumed);
        Interlocked.Exchange(ref _transferred, resumed);
        return parts;
    }

    /// <summary>
    /// The checksum the content must have: the caller's, or else the one the source reported.
    /// </summary>
    public Checksum? ExpectedChecksum() => _options.ExpectedChecksum ?? _info?.Checksum;

    /// <summary>
    /// Starts hashing the single part from its first byte, when there is a checksum to check.
    /// </summary>
    public void StartHashIfNeeded(PartRun part)
    {
        if (_hashable && _hash == null && part.Consumed == 0 && ExpectedChecksum() is { } expected)
        {
            _hash = IncrementalHash.CreateHash(expected.Algorithm);
        }
    }

    /// <summary>
    /// Adds bytes to the hash, if one is kept. A resumed attempt continues right after the bytes already hashed.
    /// </summary>
    public void Hash(ReadOnlySpan<byte> bytes) => _hash?.AppendData(bytes);

    /// <summary>
    /// The hash of everything that passed, or <c>null</c> when it wasn't kept.
    /// </summary>
    public byte[]? TakeHash() => _hashable && _hash != null ? _hash.GetHashAndReset() : null;

    public void ResetHash()
    {
        _hash?.Dispose();
        _hash = null;
    }

    public TransferProgress Snapshot()
    {
        var elapsed = Clock.Elapsed;
        var transferred = Transferred;
        var retrying = Volatile.Read(ref _retryingParts) > 0 && _state != TransferState.Completed;
        return new TransferProgress(
            retrying ? TransferState.Retrying : _state,
            transferred,
            _info?.Length,
            _rate.Sample(elapsed, transferred),
            elapsed > TimeSpan.Zero ? (transferred - Resumed) / elapsed.TotalSeconds : 0,
            elapsed,
            Volatile.Read(ref _activeParts),
            Volatile.Read(ref _completedParts),
            TotalParts,
            retrying ? Volatile.Read(ref _lastError) : null);
    }
}

/// <summary>
/// One part of a run and how far it got.
/// </summary>
internal sealed class PartRun
{
    private long _confirmed;
    private int _done;

    public PartRun(ByteRange range, long? expectedLength)
    {
        Range = range;
        ExpectedLength = expectedLength;
    }

    /// <summary>The whole part.</summary>
    public ByteRange Range { get; }

    /// <summary>How many bytes the part has, or <c>null</c> while unknown.</summary>
    public long? ExpectedLength { get; set; }

    /// <summary>The bytes of the part the target has read so far, and keeps after a failure reading the source.</summary>
    public long Consumed { get; set; }

    /// <summary>
    /// The bytes of the part the target is known to have handled, which may lag <see cref="Consumed"/> by one read.
    /// Read by checkpoints from other threads.
    /// </summary>
    public long Confirmed
    {
        get => Interlocked.Read(ref _confirmed);
        set => Interlocked.Exchange(ref _confirmed, value);
    }

    /// <summary>Whether the whole part was written. Read by checkpoints from other threads.</summary>
    public bool Done
    {
        get => Volatile.Read(ref _done) == 1;
        set => Volatile.Write(ref _done, value ? 1 : 0);
    }
}
