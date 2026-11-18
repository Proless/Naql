using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Engine;

/// <summary>
/// One run of a transfer: describes the content, plans or continues its parts, has <see cref="PartRunner"/> move
/// them, verifies the result and commits the target. It owns the target session and records how the run ended.
/// </summary>
internal sealed class TransferRun
{
    private readonly ITransferSource _source;
    private readonly ITransferTarget _target;
    private readonly TargetCapabilities _capabilities;
    private readonly TransferOptions _options;
    private readonly IProgress<TransferProgress>? _progress;
    private readonly ICheckpointStore? _store;
    private readonly RunState _state;
    private readonly PartRunner _runner;
    private TransferCheckpoint? _resumeFrom;
    private ITargetSession? _session;
    private string? _location;
    private Exception? _callerFailure;

    public TransferRun(ITransferSource source, ITransferTarget target, TransferOptions options, BandwidthLimiter[] limiters,
        IProgress<TransferProgress>? progress, TransferCheckpoint? resumeFrom, ICheckpointStore? store)
    {
        _source = source;
        _target = target;
        _capabilities = target.Capabilities ?? throw new ArgumentException("The target didn't report its capabilities", nameof(target));
        _options = options;
        _progress = progress;
        _resumeFrom = resumeFrom;
        _store = store;
        _state = new RunState(options, _capabilities, new TagList
        {
            { Telemetry.SourceType, source.GetType().Name },
            { Telemetry.TargetType, target.GetType().Name }
        });
        _runner = new PartRunner(source, options, _capabilities, limiters, _state, EnsureSessionAsync);
    }

    /// <summary>
    /// What the target has kept so far, or <c>null</c> when the transfer can't be continued later. Safe to call while
    /// the transfer runs.
    /// </summary>
    public TransferCheckpoint? GetCheckpoint() => _state.GetCheckpoint();

    public async Task<TransferResult> RunAsync(CancellationToken cancellationToken)
    {
        using var activity = Telemetry.Source.StartActivity("naql.transfer");
        activity?.SetTag(Telemetry.SourceType, _source.GetType().Name);
        activity?.SetTag(Telemetry.TargetType, _target.GetType().Name);
        activity?.SetTag("naql.max_connections", _options.MaxConnections);
        Telemetry.Active.Add(1, _state.Tags);

        CheckpointKeeper? keeper = null;
        var outcome = "failed";
        Exception? error = null;
        try
        {
            if (_store != null)
            {
                // An explicit checkpoint wins over a stored one; a store that can't load means starting over
                keeper = new CheckpointKeeper(_store, _options.CheckpointInterval, _options.ResponseTimeout, GetCheckpoint);
                _resumeFrom ??= await keeper.LoadAsync(cancellationToken).ConfigureAwait(false);
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
                await keeper.FinishAsync(_state.Completed).ConfigureAwait(false);
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
        var tags = _state.Tags;
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

        Telemetry.Active.Add(-1, _state.Tags);
        Telemetry.Transfers.Add(1, tags);
        Telemetry.Duration.Record(_state.Clock.Elapsed.TotalSeconds, tags);

        if (activity == null)
        {
            return;
        }
        activity.SetTag(Telemetry.Outcome, outcome);
        activity.SetTag("naql.bytes", _state.Transferred);
        activity.SetTag("naql.resumed", _state.Resumed);
        activity.SetTag("naql.parts", _state.TotalParts);
        activity.SetTag("naql.retries", _state.Retries);
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
        var reporter = _progress == null ? null : new ProgressReporter(_progress, _options.ProgressInterval, _state.Snapshot);

        _state.Clock.Start();
        reporter?.Start(ex =>
        {
            // The caller's progress handler failed: stop the transfer and surface that exception as it is
            Interlocked.CompareExchange(ref _callerFailure, ex, null);
            PartRunner.CancelQuietly(transfer);
        });

        try
        {
            try
            {
                await RunWithRestartAsync(transfer.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (Volatile.Read(ref _callerFailure) == null)
            {
                _state.Failure = ex;
                _state.State = cancellationToken.IsCancellationRequested ? TransferState.Canceled : TransferState.Failed;
                await StopReportingAsync(reporter, final: true).ConfigureAwait(false);
                throw;
            }
            await StopReportingAsync(reporter, final: false).ConfigureAwait(false);

            ThrowIfCallerFailed();
            // The final report of a successful transfer is the caller's to fail, like any other report
            reporter?.ReportNow();
            return new TransferResult(_state.Info!, _state.Transferred, _state.Resumed, _state.Clock.Elapsed, _state.TotalParts, _state.Retries, _state.Verified,
                _location, _state.SingleConnectionReason);
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
            _state.ResetHash();
        }
    }

    /// <summary>
    /// Stops the timed reports. For a transfer that failed or was canceled, it also sends the final report, and a
    /// handler that fails on it can't replace the error the transfer ended with.
    /// </summary>
    private async Task StopReportingAsync(ProgressReporter? reporter, bool final)
    {
        if (reporter != null)
        {
            await reporter.StopAsync().ConfigureAwait(false);
        }
        _state.Clock.Stop();
        if (final && reporter != null && Volatile.Read(ref _callerFailure) == null)
        {
            try
            {
                reporter.ReportNow();
            }
            catch (Exception)
            {
                // The transfer already ended with the error that matters
            }
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
                _state.Restart();
            }
        }
    }

    private async Task RunOnceAsync(TransferCheckpoint? checkpoint, CancellationToken token)
    {
        _state.State = TransferState.Preparing;
        _state.ResetHash();

        // A required checksum the target can't read back is verified by hashing one connection's bytes as they pass,
        // which only works from the first byte: no parallel parts, no resuming
        var hashWhileStreaming = _options.ExpectedChecksum != null && !_capabilities.Verifiable;
        if (hashWhileStreaming)
        {
            checkpoint = null;
        }

        List<PartRun>? parts = null;
        var reason = SingleConnectionReason.None;
        if (checkpoint != null || (!hashWhileStreaming && _options.MaxConnections > 1 && _capabilities.ParallelParts))
        {
            var info = await _runner.RetryAsync(TransferSide.Source, t => _runner.WithResponseTimeoutAsync(TransferSide.Source, _source.GetInfoAsync, t), token).ConfigureAwait(false);
            _state.SetInfo(info);
            if (checkpoint != null)
            {
                parts = await TryContinueAsync(checkpoint, info, token).ConfigureAwait(false);
                reason = parts?.Count == 1 ? SingleConnectionReason.ContinuedCheckpoint : SingleConnectionReason.None;
            }
            if (parts == null)
            {
                // Parts of content without a version could come from different versions and be spliced together
                var planOptions = _state.CanCombine(info) ? _options : _options with { MaxConnections = 1 };
                parts = PartPlanner.Plan(info.Length, info.SupportsRanges, _capabilities, planOptions, out reason)
                    .Select(range => new PartRun(range, range.IsAll ? info.Length : range.Length))
                    .ToList();
                if (reason == SingleConnectionReason.NotRequested && !_state.CanCombine(info))
                {
                    reason = SingleConnectionReason.NoVersion;
                }
                await OpenSessionAsync(new TargetOpenRequest(info, bufferSize: _options.BufferSize), token).ConfigureAwait(false);
            }
        }
        else
        {
            // One connection: the first read describes the content, which saves a request, and the part opens the
            // target once it has
            parts = [new(ByteRange.All, null)];
            reason = _options.MaxConnections == 1 ? SingleConnectionReason.NotRequested
                : !_capabilities.ParallelParts ? SingleConnectionReason.TargetTakesOnePart
                : SingleConnectionReason.ChecksumHashedWhileStreaming;
        }

        _state.SingleConnectionReason = reason;
        Activity.Current?.SetTag("naql.single_connection_reason", reason == SingleConnectionReason.None ? null : reason.ToString());
        _state.SetParts(parts);
        _state.State = TransferState.Transferring;
        await _runner.RunAsync(parts, token).ConfigureAwait(false);

        var transferred = _state.Transferred;
        if (_state.Info!.Length is long length && transferred != length)
        {
            throw new TransferException($"Transferred {transferred} bytes, but the source reported {length}", TransferErrorKind.Integrity, side: TransferSide.Engine);
        }

        await VerifyChecksumAsync(transferred, token).ConfigureAwait(false);

        _state.State = TransferState.Completing;
        await _runner.RetryAsync(TransferSide.Target, t => _runner.WithResponseTimeoutAsync(TransferSide.Target, _session!.CompleteAsync, t), token).ConfigureAwait(false);
        _location = _session!.Location;
        _state.Completed = true;
        _state.State = TransferState.Completed;
    }

    /// <summary>
    /// Continues the transfer a checkpoint describes, or returns <c>null</c> to start over when the source is other
    /// content, the content changed, the source can't continue it, or the target lost the partial data.
    /// </summary>
    private async Task<List<PartRun>?> TryContinueAsync(TransferCheckpoint checkpoint, SourceInfo info, CancellationToken token)
    {
        if (!_state.CanContinue(checkpoint, info))
        {
            return null;
        }

        try
        {
            await OpenSessionAsync(new TargetOpenRequest(info, checkpoint.ResumeToken, _options.BufferSize), token).ConfigureAwait(false);
        }
        catch (TransferException ex) when (ex.Kind == TransferErrorKind.NotFound)
        {
            return null;
        }
        return _state.ContinueFrom(checkpoint);
    }

    /// <summary>
    /// Checks the content against the expected checksum before the target makes it visible, and discards it on a
    /// mismatch. A checksum only the source reported is skipped when checking it would mean reading the target back.
    /// </summary>
    private async Task VerifyChecksumAsync(long length, CancellationToken token)
    {
        var expected = _state.ExpectedChecksum();
        if (expected == null)
        {
            return;
        }

        var actual = _state.TakeHash();
        if (actual == null)
        {
            if (_options.ExpectedChecksum == null || _session is not IVerifiableSession verifiable)
            {
                return;
            }
            actual = await verifiable.ComputeHashAsync(expected.Algorithm, length, token).ConfigureAwait(false);
        }

        if (!expected.Matches(actual))
        {
            // The bytes are wrong, so keeping them for a resume would only resume corrupt data
            await DiscardSessionAsync().ConfigureAwait(false);
            throw new TransferException($"The content's {expected.Algorithm.Name} is {Convert.ToHexString(actual).ToLowerInvariant()}, not {expected.ToHex()}", TransferErrorKind.Integrity, side: TransferSide.Engine);
        }
        _state.Verified = expected;
    }

    /// <summary>
    /// The session, opened for the content described so far if it isn't open yet.
    /// </summary>
    private async Task<ITargetSession> EnsureSessionAsync(CancellationToken token)
    {
        if (_session == null)
        {
            await OpenSessionAsync(new TargetOpenRequest(_state.Info!, bufferSize: _options.BufferSize), token).ConfigureAwait(false);
        }
        return _session!;
    }

    /// <summary>
    /// Opens the target, with the response timeout and the retry policy, the same way for every kind of run.
    /// </summary>
    private async Task OpenSessionAsync(TargetOpenRequest request, CancellationToken token)
    {
        var session = await _runner.RetryAsync(TransferSide.Target, t => _runner.WithResponseTimeoutAsync(TransferSide.Target, t2 => _target.OpenAsync(request, t2), t), token).ConfigureAwait(false);
        if (_capabilities.Verifiable && session is not IVerifiableSession)
        {
            // Without this check a required checksum would silently go unverified
            await DisposeQuietlyAsync(session).ConfigureAwait(false);
            var error = new TransferException($"The target {_target.GetType().Name} says it's Verifiable, but its session doesn't implement IVerifiableSession",
                TransferErrorKind.Unsupported, side: TransferSide.Target);
            throw new TransferFailedException(error, TransferSide.Target);
        }
        _session = session;
        _state.ResumeToken = _session.ResumeToken;
        _state.Discarded = false;
    }

    /// <summary>
    /// Aborts and forgets the session, so what it wrote is gone and no checkpoint refers to it.
    /// </summary>
    private async Task DiscardSessionAsync()
    {
        var session = _session;
        _session = null;
        _state.Discarded = true;
        if (session != null)
        {
            await AbortQuietlyAsync(session).ConfigureAwait(false);
            await DisposeQuietlyAsync(session).ConfigureAwait(false);
        }
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
            if (!_state.Completed && (!_capabilities.Resumable || _options.DiscardPartialOnFailure))
            {
                _state.Discarded = true;
                await AbortQuietlyAsync(session).ConfigureAwait(false);
            }
        }
        finally
        {
            await DisposeQuietlyAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Aborts a session, waiting at most <see cref="TransferOptions.ResponseTimeout"/>, so cleaning up after a failure
    /// can't hang the transfer.
    /// </summary>
    private async Task AbortQuietlyAsync(ITargetSession session)
    {
        try
        {
            await _runner.WithResponseTimeoutAsync(TransferSide.Target, session.AbortAsync, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The transfer already ended with the error that matters; failing to clean up must not replace it
        }
    }

    /// <summary>
    /// Disposes a session, waiting at most <see cref="TransferOptions.ResponseTimeout"/>. A failure to release it can't
    /// change how the transfer ended, so it's ignored.
    /// </summary>
    private async Task DisposeQuietlyAsync(ITargetSession session)
    {
        try
        {
            await _runner.WithResponseTimeoutAsync(TransferSide.Target, _ => session.DisposeAsync().AsTask(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The content is already committed, kept for a resume, or discarded
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
}
