using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Engine;

/// <summary>
/// One run of a transfer: plans the parts, moves them with retries, timeouts and throttling, then commits the target.
/// </summary>
internal sealed class TransferRun
{
    private readonly ITransferSource _source;
    private readonly ITransferTarget _target;
    private readonly TargetCapabilities _capabilities;
    private readonly TransferOptions _options;
    private readonly BandwidthLimiter[] _limiters;
    private readonly IProgress<TransferProgress>? _progress;
    private readonly ICheckpointStore? _store;
    private readonly TagList _tags;
    private readonly Stopwatch _clock = new();
    private TransferCheckpoint? _resumeFrom;
    private readonly RateMeter _rate = new();

    private volatile SourceInfo? _info;
    private volatile TransferState _state;
    private volatile List<PartRun>? _parts;
    private volatile string? _resumeToken;
    private volatile bool _discarded;
    private volatile bool _completed;
    private ITargetSession? _session;
    private long _transferred;
    private long _resumed;
    private IncrementalHash? _hash;
    private bool _hashable;
    private Checksum? _verified;
    private int _retries;
    private int _retryingParts;
    private int _activeParts;
    private int _completedParts;
    private int _totalParts;
    private Exception? _lastError;
    private Exception? _callerFailure;

    public TransferRun(ITransferSource source, ITransferTarget target, TransferOptions options, BandwidthLimiter[] limiters,
        IProgress<TransferProgress>? progress, TransferCheckpoint? resumeFrom, ICheckpointStore? store)
    {
        _source = source;
        _target = target;
        _capabilities = target.Capabilities ?? throw new ArgumentException("The target didn't report its capabilities", nameof(target));
        _options = options;
        _limiters = limiters;
        _progress = progress;
        _resumeFrom = resumeFrom;
        _store = store;
        _tags = new TagList
        {
            { Telemetry.SourceType, source.GetType().Name },
            { Telemetry.TargetType, target.GetType().Name }
        };
    }

    /// <summary>
    /// What the target has kept so far, or <c>null</c> when the transfer can't be continued later: it finished, its
    /// partial data was discarded, the target can't resume, or the source has no version or ranges to check and
    /// continue with. Safe to call while the transfer runs.
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
        return new TransferCheckpoint(info.Length, info.Version, token, parts.Select(part =>
        {
            var range = part.Range.IsAll ? new ByteRange(0, info.Length) : part.Range;
            var completed = part.Done && range.Length is long length ? length : resumableParts ? part.Confirmed : 0;
            return new CheckpointPart(range, Math.Min(completed, range.Length ?? completed));
        }));
    }

    public async Task<TransferResult> RunAsync(CancellationToken cancellationToken)
    {
        using var activity = Telemetry.Source.StartActivity("naql.transfer");
        activity?.SetTag(Telemetry.SourceType, _source.GetType().Name);
        activity?.SetTag(Telemetry.TargetType, _target.GetType().Name);
        activity?.SetTag("naql.max_connections", _options.MaxConnections);
        Telemetry.Active.Add(1, _tags);

        CheckpointKeeper? keeper = null;
        var outcome = "failed";
        Exception? error = null;
        try
        {
            if (_store != null)
            {
                // An explicit checkpoint wins over a stored one
                _resumeFrom ??= await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
                keeper = new CheckpointKeeper(_store, _options.CheckpointInterval, GetCheckpoint);
                keeper.Start();
            }

            TransferResult result;
            try
            {
                result = await RunCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (TransferException ex) when (ex is not TransferFailedException)
            {
                // Errors the engine raises itself, such as a failed length check, end the transfer the same way
                throw new TransferFailedException(ex, ex.Side);
            }
            outcome = "completed";
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "canceled";
            throw;
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            if (keeper != null)
            {
                await keeper.FinishAsync(_completed).ConfigureAwait(false);
                if (keeper.LastError is { } storeError)
                {
                    activity?.AddEvent(new ActivityEvent("naql.checkpoint.error", tags: new ActivityTagsCollection
                    {
                        ["exception.type"] = storeError.GetType().FullName,
                        ["exception.message"] = storeError.Message
                    }));
                }
            }
            RecordOutcome(activity, outcome, error);
        }
    }

    /// <summary>
    /// Finishes the transfer's activity and metrics with how it ended.
    /// </summary>
    private void RecordOutcome(Activity? activity, string outcome, Exception? error)
    {
        var tags = _tags;
        tags.Add(Telemetry.Outcome, outcome);
        var failure = error as TransferException;
        if (failure != null)
        {
            tags.Add(Telemetry.ErrorType, failure.Kind.ToString());
        }
        else if (error != null)
        {
            tags.Add(Telemetry.ErrorType, error.GetType().FullName);
        }

        Telemetry.Active.Add(-1, _tags);
        Telemetry.Transfers.Add(1, tags);
        Telemetry.Duration.Record(_clock.Elapsed.TotalSeconds, tags);

        if (activity == null)
        {
            return;
        }
        activity.SetTag(Telemetry.Outcome, outcome);
        activity.SetTag("naql.bytes", Interlocked.Read(ref _transferred));
        activity.SetTag("naql.resumed", Interlocked.Read(ref _resumed));
        activity.SetTag("naql.parts", Volatile.Read(ref _totalParts));
        activity.SetTag("naql.retries", Volatile.Read(ref _retries));
        if (failure != null)
        {
            activity.SetTag(Telemetry.ErrorType, failure.Kind.ToString());
            activity.SetTag(Telemetry.ErrorSide, failure.Side.ToString());
            activity.SetTag(Telemetry.ErrorTransient, failure.IsTransient);
        }
        else if (error != null)
        {
            activity.SetTag(Telemetry.ErrorType, error.GetType().FullName);
        }
        if (error != null)
        {
            activity.SetStatus(ActivityStatusCode.Error, error.Message);
        }
        else if (outcome == "completed")
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
    }

    private async Task<TransferResult> RunCoreAsync(CancellationToken cancellationToken)
    {
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reporter = _progress == null ? null : new ProgressReporter(_progress, _options.ProgressInterval, Snapshot);

        _clock.Start();
        reporter?.Start(ex =>
        {
            // The caller's progress handler failed: stop the transfer and surface that exception as it is
            Interlocked.CompareExchange(ref _callerFailure, ex, null);
            CancelQuietly(transfer);
        });

        try
        {
            try
            {
                await RunWithRestartAsync(transfer.Token).ConfigureAwait(false);
            }
            finally
            {
                if (reporter != null)
                {
                    await reporter.StopAsync().ConfigureAwait(false);
                }
            }

            ThrowIfCallerFailed();
            _clock.Stop();
            reporter?.ReportNow();
            return new TransferResult(_info!, Interlocked.Read(ref _transferred), Interlocked.Read(ref _resumed), _clock.Elapsed, _totalParts, _retries, _verified);
        }
        catch (Exception) when (Volatile.Read(ref _callerFailure) != null)
        {
            ThrowIfCallerFailed();
            throw;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested && ex.CancellationToken != cancellationToken)
        {
            throw new OperationCanceledException("The transfer was canceled", ex, cancellationToken);
        }
        finally
        {
            await CloseSessionAsync().ConfigureAwait(false);
            ResetHash();
        }
    }

    private async Task RunWithRestartAsync(CancellationToken token)
    {
        for (var restarted = false; ; restarted = true)
        {
            try
            {
                // A checkpoint only applies to the first run: after a restart the content is new
                await RunOnceAsync(restarted ? null : _resumeFrom, token).ConfigureAwait(false);
                return;
            }
            catch (TransferException ex) when (ex.Kind == TransferErrorKind.SourceChanged && !restarted && !token.IsCancellationRequested)
            {
                // What was written belongs to the old content: discard it and start over, once
                Activity.Current?.AddEvent(new ActivityEvent("naql.restart", tags: new ActivityTagsCollection { ["exception.message"] = ex.Message }));
                await DiscardSessionAsync().ConfigureAwait(false);

                _info = null;
                _parts = null;
                Interlocked.Exchange(ref _transferred, 0);
                Interlocked.Exchange(ref _resumed, 0);
                Interlocked.Exchange(ref _completedParts, 0);
                Interlocked.Increment(ref _retries);
                _rate.Reset();
            }
        }
    }

    private async Task RunOnceAsync(TransferCheckpoint? checkpoint, CancellationToken token)
    {
        _state = TransferState.Preparing;
        ResetHash();

        // A required checksum the target can't read back is verified by hashing one connection's bytes as they pass,
        // which only works from the first byte: no parallel parts, no resuming
        var hashWhileStreaming = _options.ExpectedChecksum != null && !_capabilities.Verifiable;
        if (hashWhileStreaming)
        {
            checkpoint = null;
        }

        List<PartRun>? parts = null;
        if (checkpoint != null || (!hashWhileStreaming && _options.MaxConnections > 1 && _capabilities.ParallelParts))
        {
            var info = await RetryAsync(TransferSide.Source, t => WithResponseTimeoutAsync(_source.GetInfoAsync, t), token).ConfigureAwait(false);
            SetInfo(info);
            if (checkpoint != null)
            {
                parts = await TryResumeAsync(checkpoint, info, token).ConfigureAwait(false);
            }
            if (parts == null)
            {
                // Parts of content without a version could come from different versions and be spliced together
                var planOptions = CanCombine(info) ? _options : _options with { MaxConnections = 1 };
                parts = PartPlanner.Plan(info.Length, info.SupportsRanges, _capabilities, planOptions)
                    .Select(range => new PartRun(range, range.IsAll ? info.Length : range.Length))
                    .ToList();
                await OpenSessionAsync(new TargetOpenRequest(info), token).ConfigureAwait(false);
            }
        }
        else
        {
            // One connection: the first read describes the content, which saves a request
            parts = [new(ByteRange.All, null)];
        }

        // One part from its first byte can be hashed as it passes; anything else needs the target to read it back
        _hashable = parts.Count == 1 && parts[0].Consumed == 0;
        _parts = parts;
        Volatile.Write(ref _totalParts, parts.Count);
        Volatile.Write(ref _completedParts, parts.Count(part => part.Done));
        _state = TransferState.Transferring;
        await RunPartsAsync(parts, token).ConfigureAwait(false);

        var transferred = Interlocked.Read(ref _transferred);
        if (_info!.Length is long length && transferred != length)
        {
            throw new TransferException($"Transferred {transferred} bytes, but the source reported {length}", TransferErrorKind.Integrity, side: TransferSide.Engine);
        }

        await VerifyChecksumAsync(transferred, token).ConfigureAwait(false);

        _state = TransferState.Completing;
        await RetryAsync(TransferSide.Target, async t =>
        {
            await _session!.CompleteAsync(t).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
        _completed = true;
        _state = TransferState.Completed;
    }

    /// <summary>
    /// Continues the transfer a checkpoint describes, or returns <c>null</c> to start over when the content changed,
    /// the source can't continue it, or the target lost the partial data.
    /// </summary>
    private async Task<List<PartRun>?> TryResumeAsync(TransferCheckpoint checkpoint, SourceInfo info, CancellationToken token)
    {
        if (!_capabilities.Resumable || checkpoint.ResumeToken == null || !info.SupportsRanges ||
            info.Version == null || info.Version != checkpoint.SourceVersion || info.Length != checkpoint.Length)
        {
            return null;
        }

        try
        {
            await OpenSessionAsync(new TargetOpenRequest(info, checkpoint.ResumeToken), token).ConfigureAwait(false);
        }
        catch (TransferException ex) when (ex.Kind == TransferErrorKind.NotFound)
        {
            return null;
        }

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
    private Checksum? ExpectedChecksum() => _options.ExpectedChecksum ?? _info?.Checksum;

    /// <summary>
    /// Checks the content against the expected checksum before the target makes it visible, and discards it on a
    /// mismatch. A checksum only the source reported is skipped when checking it would mean reading the target back.
    /// </summary>
    private async Task VerifyChecksumAsync(long length, CancellationToken token)
    {
        var expected = ExpectedChecksum();
        if (expected == null)
        {
            return;
        }

        byte[]? actual;
        if (_hashable && _hash != null)
        {
            actual = _hash.GetHashAndReset();
        }
        else if (_options.ExpectedChecksum != null && _session is IVerifiableSession verifiable)
        {
            actual = await verifiable.ComputeHashAsync(expected.Algorithm, length, token).ConfigureAwait(false);
        }
        else
        {
            return;
        }

        if (!expected.Matches(actual))
        {
            // The bytes are wrong, so keeping them for a resume would only resume corrupt data
            await DiscardSessionAsync().ConfigureAwait(false);
            throw new TransferException($"The content's {expected.Algorithm.Name} is {Convert.ToHexString(actual).ToLowerInvariant()}, not {expected.ToHex()}", TransferErrorKind.Integrity, side: TransferSide.Engine);
        }
        _verified = expected;
    }

    /// <summary>
    /// Starts hashing the single part from its first byte, when there is a checksum to check.
    /// </summary>
    private void StartHashIfNeeded(PartRun part)
    {
        if (_hashable && _hash == null && part.Consumed == 0 && ExpectedChecksum() is { } expected)
        {
            _hash = IncrementalHash.CreateHash(expected.Algorithm);
        }
    }

    private void ResetHash()
    {
        _hash?.Dispose();
        _hash = null;
    }

    /// <summary>
    /// Aborts and forgets the session, so what it wrote is gone and no checkpoint refers to it.
    /// </summary>
    private async Task DiscardSessionAsync()
    {
        var session = _session;
        _session = null;
        _discarded = true;
        if (session != null)
        {
            await AbortQuietlyAsync(session).ConfigureAwait(false);
            session.Dispose();
        }
    }

    private async Task OpenSessionAsync(TargetOpenRequest request, CancellationToken token)
    {
        _session = await RetryAsync(TransferSide.Target, t => _target.OpenAsync(request, t), token).ConfigureAwait(false);
        _resumeToken = _session.ResumeToken;
        _discarded = false;
    }

    private async Task RunPartsAsync(List<PartRun> parts, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(_options.MaxConnections);
        using var partsCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var failures = new List<PartFailure>();

        async Task RunGuardedAsync(PartRun part)
        {
            try
            {
                await gate.WaitAsync(partsCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Interlocked.Increment(ref _activeParts);
            using var activity = Telemetry.Source.StartActivity("naql.part");
            activity?.SetTag("naql.part.start", part.Range.Start);
            activity?.SetTag("naql.part.length", part.Range.Length);
            activity?.SetTag("naql.part.resumed", part.Consumed);
            try
            {
                var failure = await RunPartAsync(part, partsCts.Token).ConfigureAwait(false);
                if (failure == null)
                {
                    Interlocked.Increment(ref _completedParts);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                else
                {
                    // One failed part fails the transfer, so stop the others
                    lock (failures)
                    {
                        failures.Add(failure);
                    }
                    CancelQuietly(partsCts);
                    activity?.SetTag(Telemetry.ErrorType, failure.Error.Kind.ToString());
                    activity?.SetTag(Telemetry.ErrorSide, failure.Side.ToString());
                    activity?.SetStatus(ActivityStatusCode.Error, failure.Error.Message);
                }
            }
            catch (OperationCanceledException) when (partsCts.IsCancellationRequested)
            {
                // Stopped because of another part's failure, or because the transfer was canceled
                activity?.SetTag(Telemetry.Outcome, "canceled");
            }
            finally
            {
                activity?.SetTag("naql.part.bytes", part.Consumed);
                Interlocked.Decrement(ref _activeParts);
                gate.Release();
            }
        }

        // Parts a checkpoint says are done are skipped
        await Task.WhenAll(parts.Where(part => !part.Done).Select(RunGuardedAsync)).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        if (failures.Count > 0)
        {
            var first = failures[0];
            throw new TransferFailedException(first.Error, first.Side, failures.ToArray());
        }
    }

    /// <summary>
    /// Runs a part, retrying it as the policy allows.
    /// </summary>
    /// <returns><c>null</c> when the part is done, otherwise why it failed for good</returns>
    private async Task<PartFailure?> RunPartAsync(PartRun part, CancellationToken token)
    {
        for (var attempts = 1; ; attempts++)
        {
            var failure = await TryPartAsync(part, token).ConfigureAwait(false);
            if (failure == null)
            {
                part.Confirmed = part.Consumed;
                part.Done = true;
                return null;
            }

            if (!failure.Resume && part.Consumed > 0)
            {
                // The part starts over, so its bytes no longer count, and neither does their hash
                Interlocked.Add(ref _transferred, -part.Consumed);
                part.Consumed = 0;
                part.Confirmed = 0;
                ResetHash();
            }

            if (!CanRetry(failure.Error, checkRepeatable: true) || attempts > _options.Retry.MaxRetries)
            {
                var range = part.Range.IsAll && _info?.Length is long length ? new ByteRange(0, length) : part.Range;
                return new PartFailure(range, failure.Error, failure.Side, attempts);
            }

            await WaitBeforeRetryAsync(failure.Error, failure.Side, attempts, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Makes one attempt at a part.
    /// </summary>
    /// <returns><c>null</c> when the part is done, otherwise what went wrong and whether the part can continue where it stopped</returns>
    private async Task<AttemptFailure?> TryPartAsync(PartRun part, CancellationToken token)
    {
        var range = part.Range.Skip(part.Consumed);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        SourceStream? source = null;
        MeteredStream? metered = null;
        IdleWatchdog? watchdog = null;
        try
        {
            source = await WithResponseTimeoutAsync(t => _source.OpenReadAsync(new ReadRequest(range, _info?.Version), t), attempt.Token).ConfigureAwait(false);
            if (_info == null)
            {
                SetInfo(source.Info);
            }
            if (part.Range.IsAll)
            {
                part.ExpectedLength ??= _info!.Length;
            }
            if (_session == null)
            {
                _session = await _target.OpenAsync(new TargetOpenRequest(_info!), attempt.Token).ConfigureAwait(false);
                _resumeToken = _session.ResumeToken;
                _discarded = false;
            }

            // Bytes kept by earlier attempts stay kept; this attempt's bytes count once the target asks for more
            var keptBefore = part.Consumed;
            part.Confirmed = keptBefore;
            StartHashIfNeeded(part);
            metered = new MeteredStream(source.Content, part.ExpectedLength - part.Consumed, _options.BufferSize, _limiters, bytes =>
            {
                // A resumed attempt continues right after the bytes already hashed, so the hash stays in order
                _hash?.AppendData(bytes.Span);
                part.Consumed += bytes.Length;
                Interlocked.Add(ref _transferred, bytes.Length);
                Telemetry.Bytes.Add(bytes.Length, _tags);
            }, confirmed => part.Confirmed = keptBefore + confirmed, _clock);

            // Disposing the source unblocks a read that ignores cancellation
            var opened = source;
            using var stopReads = attempt.Token.Register(() => opened.Dispose());
            watchdog = IdleWatchdog.Start(metered, _options.IdleTimeout, attempt, _clock);

            await _session.WritePartAsync(range, metered, attempt.Token).ConfigureAwait(false);
            if (!metered.ReachedEnd)
            {
                var error = new TransferException("The target finished the part without reading all of it", TransferErrorKind.Integrity, side: TransferSide.Target);
                return new AttemptFailure(error, TransferSide.Target, resume: false);
            }
            return null;
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
            {
                throw new OperationCanceledException("The transfer was canceled", ex, token);
            }
            if (watchdog?.TimedOut == true)
            {
                var side = watchdog.StalledSide;
                var error = new TransferException($"No data moved for {_options.IdleTimeout.TotalSeconds:0.###} seconds", TransferErrorKind.Timeout,
                    isTransient: true, side, innerException: ex);
                return new AttemptFailure(error, side, resume: side == TransferSide.Source && CanResumePart());
            }
            if (metered?.ReadFailure is Exception readFailure)
            {
                // The source failed while the target was reading: the target kept what it read
                return Failed(readFailure, TransferSide.Source, resume: CanResumePart());
            }
            if (metered == null)
            {
                // Nothing was written in this attempt, so earlier progress still holds
                return Failed(ex, source == null ? TransferSide.Source : TransferSide.Target, resume: CanResumePart());
            }
            // The target failed on its own, so what it read may not have been kept
            return Failed(ex, TransferSide.Target, resume: false);
        }
        finally
        {
            watchdog?.Dispose();
            source?.Dispose();
        }
    }

    /// <summary>
    /// What one attempt at a part failed with, and whether the next attempt can continue where it stopped.
    /// </summary>
    private static AttemptFailure Failed(Exception exception, TransferSide side, bool resume)
    {
        var (error, observed) = Classify(exception, side);
        return new AttemptFailure(error, observed, resume);
    }

    private async Task<T> RetryAsync<T>(TransferSide side, Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        for (var attempts = 1; ; attempts++)
        {
            TransferException error;
            TransferSide observed;
            try
            {
                return await operation(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                (error, observed) = Classify(ex, side);
                if (!CanRetry(error, checkRepeatable: false) || attempts > _options.Retry.MaxRetries)
                {
                    throw new TransferFailedException(error, observed);
                }
            }

            await WaitBeforeRetryAsync(error, observed, attempts, token).ConfigureAwait(false);
        }
    }

    private async Task WaitBeforeRetryAsync(TransferException error, TransferSide side, int retry, CancellationToken token)
    {
        Interlocked.Increment(ref _retries);
        Volatile.Write(ref _lastError, error);
        Interlocked.Increment(ref _retryingParts);
        try
        {
            var delay = _options.Retry.GetDelay(retry, error.RetryDelay);

            var tags = _tags;
            tags.Add(Telemetry.ErrorType, error.Kind.ToString());
            tags.Add(Telemetry.ErrorSide, side.ToString());
            Telemetry.Retries.Add(1, tags);
            // The current activity is the part's, or the transfer's for operations outside parts
            Activity.Current?.AddEvent(new ActivityEvent("naql.retry", tags: new ActivityTagsCollection
            {
                ["naql.retry.number"] = retry,
                ["naql.retry.delay_ms"] = delay.TotalMilliseconds,
                [Telemetry.ErrorType] = error.Kind.ToString(),
                [Telemetry.ErrorSide] = side.ToString(),
                ["exception.message"] = error.Message
            }));

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _retryingParts);
        }
    }

    /// <summary>
    /// Waits for the source to respond, at most <see cref="TransferOptions.ResponseTimeout"/>, even when it ignores cancellation.
    /// </summary>
    private async Task<T> WithResponseTimeoutAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token) where T : class
    {
        var timeout = _options.ResponseTimeout;
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return await operation(token).ConfigureAwait(false);
        }

        // The operation is asked to stop at the timeout, and WaitAsync stops waiting for it even if it doesn't
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);
        var task = operation(timeoutCts.Token);
        try
        {
            return await task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            DisposeWhenDone(task);
            token.ThrowIfCancellationRequested();
            throw ResponseTimeout(ex);
        }
    }

    /// <summary>
    /// Disposes the result of an operation the engine stopped waiting for, if it still arrives.
    /// </summary>
    private static void DisposeWhenDone<T>(Task<T> task)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                (t.Result as IDisposable)?.Dispose();
            }
            else
            {
                _ = t.Exception;
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private TransferException ResponseTimeout(Exception? inner)
    {
        return new TransferException($"The source didn't respond within {_options.ResponseTimeout.TotalSeconds:0.###} seconds", TransferErrorKind.Timeout, isTransient: true, TransferSide.Source, innerException: inner);
    }

    private void SetInfo(SourceInfo info)
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
    /// Whether a failed part can continue where it stopped. Continuing joins bytes from two requests, which is only safe
    /// when they are known to be the same content.
    /// </summary>
    private bool CanResumePart() => _capabilities.ResumableParts && _info is { SupportsRanges: true } info && CanCombine(info);

    /// <summary>
    /// Whether bytes from separate requests can be joined into one result: the source names a version every request
    /// is checked against, or a required checksum would catch content that changed in between.
    /// </summary>
    private bool CanCombine(SourceInfo info) => info.Version != null || _options.ExpectedChecksum != null;

    private bool CanRetry(TransferException error, bool checkRepeatable)
    {
        return error.IsTransient &&
               error.Kind != TransferErrorKind.SourceChanged &&
               (!checkRepeatable || _info?.IsRepeatable != false);
    }

    /// <summary>
    /// Turns any exception from a source or target into a transfer error, and decides which side it belongs to: the
    /// side a <see cref="TransferException"/> names, or else the side the engine was calling.
    /// </summary>
    private static (TransferException Error, TransferSide Side) Classify(Exception ex, TransferSide side)
    {
        return ex switch
        {
            TransferException transfer => (transfer, transfer.Side == TransferSide.Unknown ? side : transfer.Side),
            TimeoutException => (new TransferException(ex.Message, TransferErrorKind.Timeout, isTransient: true, side, innerException: ex), side),
            // HttpClient reports its own timeout as a canceled task; other cancellations the caller didn't ask for are bugs
            TaskCanceledException => (new TransferException("The operation timed out", TransferErrorKind.Timeout, isTransient: true, side, innerException: ex), side),
            OperationCanceledException => (new TransferException(ex.Message, TransferErrorKind.Unknown, isTransient: false, side, innerException: ex), side),
            IOException => (new TransferException(ex.Message, TransferErrorKind.Network, isTransient: true, side, innerException: ex), side),
            _ => (new TransferException(ex.Message, TransferErrorKind.Unknown, isTransient: false, side, innerException: ex), side)
        };
    }

    private TransferProgress Snapshot()
    {
        var elapsed = _clock.Elapsed;
        var transferred = Interlocked.Read(ref _transferred);
        var retrying = Volatile.Read(ref _retryingParts) > 0 && _state != TransferState.Completed;
        return new TransferProgress(
            retrying ? TransferState.Retrying : _state,
            transferred,
            _info?.Length,
            _rate.Sample(elapsed, transferred),
            elapsed > TimeSpan.Zero ? (transferred - Interlocked.Read(ref _resumed)) / elapsed.TotalSeconds : 0,
            elapsed,
            Volatile.Read(ref _activeParts),
            Volatile.Read(ref _completedParts),
            Volatile.Read(ref _totalParts),
            retrying ? Volatile.Read(ref _lastError) : null);
    }

    private async Task CloseSessionAsync()
    {
        var session = _session;
        if (session == null)
        {
            return;
        }
        _session = null;

        try
        {
            if (!_completed && (!_capabilities.Resumable || _options.DiscardPartialOnFailure))
            {
                _discarded = true;
                await AbortQuietlyAsync(session).ConfigureAwait(false);
            }
        }
        finally
        {
            session.Dispose();
        }
    }

    private static async Task AbortQuietlyAsync(ITargetSession session)
    {
        try
        {
            await session.AbortAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The transfer already ended with the error that matters; failing to clean up must not replace it
        }
    }

    private void ThrowIfCallerFailed()
    {
        var failure = Volatile.Read(ref _callerFailure);
        if (failure != null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    private static void CancelQuietly(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished
        }
    }

    private sealed class PartRun
    {
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

        private long _confirmed;
        private int _done;
    }

    private sealed class AttemptFailure
    {
        public AttemptFailure(TransferException error, TransferSide side, bool resume)
        {
            Error = error;
            Side = side;
            Resume = resume;
        }

        public TransferException Error { get; }

        /// <summary>The side the engine saw the failure on.</summary>
        public TransferSide Side { get; }

        /// <summary>Whether the next attempt can continue after the bytes the target kept.</summary>
        public bool Resume { get; }
    }
}
