using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace Naqel.Engine;

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
    // Splitting a part changes two ranges and the list together; checkpoints and progress must see all or none of it
    private readonly object _partsLock = new();

    private volatile SourceInfo? _info;
    private volatile TransferState _state;
    private volatile List<PartRun>? _parts; // replaced, never changed, so readers can hold on to it
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
    private IAcknowledgingSession? _acknowledging;
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
    /// Why the run uses one connection, or <see cref="SingleConnectionReason.None"/>.
    /// </summary>
    public SingleConnectionReason SingleConnectionReason { get; set; }

    /// <summary>
    /// The error the run failed with, which the final progress report shows.
    /// </summary>
    public Exception? Failure { get; set; }

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
        lock (_partsLock)
        {
            _hashable = parts.Count == 1 && parts[0].Consumed == 0;
            _parts = parts;
            Volatile.Write(ref _totalParts, parts.Count);
            Volatile.Write(ref _completedParts, parts.Count(part => part.Done));
        }
    }

    /// <summary>
    /// Gives the end of the running part with the most bytes left to a new part, for a connection that freed up. The
    /// running part's stream ends sooner, so no byte is moved twice, and the new part follows it in the list, which keeps
    /// the parts in order for checkpoints.
    /// </summary>
    /// <returns>The new part, or <c>null</c> when no part can be split: splitting is off, the length isn't known, the source can't read
    /// ranges or its parts can't be combined, the target can't take it, a hash needs the bytes in order, or no running
    /// part has enough left</returns>
    public PartRun? TrySplit()
    {
        var info = _info;
        if (!_options.AdaptiveSplitting || info?.Length is not long length || !info.SupportsRanges || !CanCombine(info) ||
            !_capabilities.ParallelParts || !_capabilities.SplittableParts || IsHashing || Acknowledging != null)
        {
            return null;
        }

        lock (_partsLock)
        {
            var parts = _parts;
            if (parts == null || parts.Count >= (_capabilities.MaxParts ?? int.MaxValue))
            {
                return null;
            }

            var best = -1;
            long bestStart = 0;
            long bestFrom = 0;
            long bestEnd = 0;
            PartAttempt? bestAttempt = null;
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var range = part.Range.IsAll ? new ByteRange(0, length) : part.Range;
                if (part.Done || part.Attempt is not { } attempt || range.Length is not long partLength)
                {
                    continue;
                }
                var from = range.Start + attempt.Offset + attempt.Stream.ReservedEnd;
                var end = range.Start + partLength;
                if (best < 0 || end - from > bestEnd - bestFrom)
                {
                    (best, bestStart, bestFrom, bestEnd, bestAttempt) = (i, range.Start, from, end, attempt);
                }
            }
            if (best < 0 ||
                PartPlanner.SplitAt(bestStart, bestFrom, bestEnd, _capabilities, _options) is not long at ||
                !bestAttempt!.Stream.TryEndAt(at - bestStart - bestAttempt.Offset))
            {
                return null;
            }

            var split = parts[best];
            split.Range = new ByteRange(bestStart, at - bestStart);
            split.ExpectedLength = at - bestStart;
            var next = new PartRun(new ByteRange(at, bestEnd - at), bestEnd - at);
            var updated = new List<PartRun>(parts);
            updated.Insert(best + 1, next);
            // Only a single part is hashed as it passes, and the content no longer is one
            _hashable = false;
            _parts = updated;
            Interlocked.Increment(ref _totalParts);
            return next;
        }
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
        var acknowledging = Acknowledging;
        CheckpointPart[] saved;
        lock (_partsLock)
        {
            // A split may have replaced the list since
            if (_parts is not { } current)
            {
                return null;
            }
            saved = current.Select(part =>
            {
                var range = part.Range.IsAll ? new ByteRange(0, info.Length) : part.Range;
                // An acknowledging session says what it kept; otherwise what the session asked for again counts
                var completed = part.Done && range.Length is long length ? length
                    : !resumableParts ? 0
                    : acknowledging != null ? Acknowledged(acknowledging, range)
                    : part.Confirmed;
                return new CheckpointPart(range, Math.Min(completed, range.Length ?? completed));
            }).ToArray();
        }
        return new TransferCheckpoint(info.Length, TransferCheckpoint.Fingerprint(info.Identity), info.Version, token, saved);
    }

    /// <summary>
    /// The target's session when it keeps only what its other side acknowledged, for as long as the run might still
    /// make a checkpoint, also after the session is closed.
    /// </summary>
    public IAcknowledgingSession? Acknowledging
    {
        get => Volatile.Read(ref _acknowledging);
        set => Volatile.Write(ref _acknowledging, value);
    }

    /// <summary>
    /// How much of <paramref name="range"/>, from its start, the session acknowledged.
    /// </summary>
    public static long Acknowledged(IAcknowledgingSession session, ByteRange range)
    {
        var kept = Math.Max(0, session.Kept(range.Start));
        return range.Length is long length ? Math.Min(kept, length) : kept;
    }

    /// <summary>
    /// Whether the bytes of a part pass through a hash, which can only go forward: a part that goes back to fewer kept
    /// bytes then has to start over.
    /// </summary>
    public bool IsHashing => _hash != null;

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
        // An acknowledging session checked with its server when it reopened, so what it says wins over the checkpoint,
        // which may be older, or count bytes the server since lost
        var acknowledging = Acknowledging;
        var parts = checkpoint.Parts.Select(saved =>
        {
            var acknowledged = acknowledging != null ? Acknowledged(acknowledging, saved.Range) : saved.Completed;
            var done = saved.Range.Length is long length && acknowledged == length;
            var kept = done || _capabilities.ResumableParts ? acknowledged : 0;
            return new PartRun(saved.Range, saved.Range.Length) { Consumed = kept, Confirmed = kept, Done = done };
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
        var state = _state;
        var ended = state is TransferState.Completed or TransferState.Failed or TransferState.Canceled;
        var retrying = Volatile.Read(ref _retryingParts) > 0 && !ended;
        return new TransferProgress(
            retrying ? TransferState.Retrying : state,
            transferred,
            _info?.Length,
            _rate.Sample(elapsed, transferred),
            elapsed > TimeSpan.Zero ? (transferred - Resumed) / elapsed.TotalSeconds : 0,
            elapsed,
            Volatile.Read(ref _activeParts),
            Volatile.Read(ref _completedParts),
            TotalParts,
            retrying ? Volatile.Read(ref _lastError) : state == TransferState.Failed ? Failure : null,
            PartsProgress());
    }

    private PartProgress[] PartsProgress()
    {
        var parts = _parts;
        if (parts == null)
        {
            return Array.Empty<PartProgress>();
        }
        var length = _info?.Length;
        lock (_partsLock)
        {
            return (_parts ?? parts).Select(part => new PartProgress(part.Range.IsAll && length is long total ? new ByteRange(0, total) : part.Range, part.Consumed,
                part.Done ? PartState.Done : part.State)).ToArray();
        }
    }
}

/// <summary>
/// One part of a run and how far it got.
/// </summary>
internal sealed class PartRun
{
    private long _confirmed;
    private int _done;
    private long _consumed;
    private int _state;

    private readonly object _lock = new();
    private ByteRange _range;
    private long? _expectedLength;
    private PartAttempt? _attempt;

    public PartRun(ByteRange range, long? expectedLength)
    {
        _range = range;
        _expectedLength = expectedLength;
    }

    /// <summary>The whole part, which ends sooner once it was split.</summary>
    public ByteRange Range
    {
        get
        {
            lock (_lock)
            {
                return _range;
            }
        }
        set
        {
            lock (_lock)
            {
                _range = value;
            }
        }
    }

    /// <summary>How many bytes the part has, or <c>null</c> while unknown.</summary>
    public long? ExpectedLength
    {
        get
        {
            lock (_lock)
            {
                return _expectedLength;
            }
        }
        set
        {
            lock (_lock)
            {
                _expectedLength = value;
            }
        }
    }

    /// <summary>
    /// The attempt moving the part right now, whose stream a split can end sooner, or <c>null</c> between attempts.
    /// </summary>
    public PartAttempt? Attempt
    {
        get => Volatile.Read(ref _attempt);
        set => Volatile.Write(ref _attempt, value);
    }

    /// <summary>
    /// The bytes of the part the target has read so far, and keeps after a failure reading the source. Read by progress
    /// from other threads.
    /// </summary>
    public long Consumed
    {
        get => Interlocked.Read(ref _consumed);
        set => Interlocked.Exchange(ref _consumed, value);
    }

    /// <summary>What the part is doing, for progress.</summary>
    public PartState State
    {
        get => (PartState)Volatile.Read(ref _state);
        set => Volatile.Write(ref _state, (int)value);
    }

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

/// <summary>
/// An attempt at a part: the stream the target reads, which starts <see cref="Offset"/> bytes into the part.
/// </summary>
internal sealed class PartAttempt
{
    public PartAttempt(MeteredStream stream, long offset)
    {
        Stream = stream;
        Offset = offset;
    }

    public MeteredStream Stream { get; }

    public long Offset { get; }
}
