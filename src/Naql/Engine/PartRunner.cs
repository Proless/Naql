using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Engine;

/// <summary>
/// Moves parts from the source to the target session, with retries, timeouts and throttling, and gives the rest of
/// the run the same retries and timeouts for its own calls to the source and target.
/// </summary>
internal sealed class PartRunner
{
    private readonly ITransferSource _source;
    private readonly TransferOptions _options;
    private readonly TargetCapabilities _capabilities;
    private readonly BandwidthLimiter[] _limiters;
    private readonly RunState _state;
    private readonly Func<CancellationToken, Task<ITargetSession>> _session;

    /// <param name="source">Where the parts come from</param>
    /// <param name="options">The transfer's settings</param>
    /// <param name="capabilities">What the target can do</param>
    /// <param name="limiters">The rate limits every read waits for</param>
    /// <param name="state">The run's shared state</param>
    /// <param name="session">Returns the target session, opening it with the content's info the first time</param>
    public PartRunner(ITransferSource source, TransferOptions options, TargetCapabilities capabilities, BandwidthLimiter[] limiters, RunState state,
        Func<CancellationToken, Task<ITargetSession>> session)
    {
        _source = source;
        _options = options;
        _capabilities = capabilities;
        _limiters = limiters;
        _state = state;
        _session = session;
    }

    /// <summary>
    /// Moves the parts that aren't done yet, at most <paramref name="connections"/> at a time. One part
    /// that fails for good stops the others.
    /// </summary>
    /// <exception cref="TransferFailedException">A part failed, with every part's failure</exception>
    public async Task RunAsync(IReadOnlyList<PartRun> parts, int connections, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(connections);
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

            _state.PartStarted();
            using var activity = Telemetry.Source.StartActivity("naql.part");
            activity?.SetTag("naql.part.start", part.Range.Start);
            activity?.SetTag("naql.part.length", part.Range.Length);
            activity?.SetTag("naql.part.resumed", part.Consumed);
            try
            {
                var failure = await RunPartAsync(part, partsCts.Token).ConfigureAwait(false);
                if (failure == null)
                {
                    _state.PartCompleted();
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                else
                {
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
                _state.PartStopped();
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
    /// Runs an operation outside the parts, such as opening the target, and retries it as the policy allows.
    /// </summary>
    /// <param name="side">The side the operation calls, for errors that don't name one</param>
    /// <param name="operation">The call to make</param>
    /// <param name="token">Stops retrying</param>
    /// <exception cref="TransferFailedException">The operation failed for good</exception>
    public async Task<T> RetryAsync<T>(TransferSide side, Func<CancellationToken, Task<T>> operation, CancellationToken token)
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

    /// <inheritdoc cref="RetryAsync{T}"/>
    public Task RetryAsync(TransferSide side, Func<CancellationToken, Task> operation, CancellationToken token)
    {
        return RetryAsync(side, async t =>
        {
            await operation(t).ConfigureAwait(false);
            return Done;
        }, token);
    }

    /// <summary>
    /// Waits for a source or target to respond, at most <see cref="TransferOptions.ResponseTimeout"/>, even when it
    /// ignores cancellation.
    /// </summary>
    public async Task<T> WithResponseTimeoutAsync<T>(TransferSide side, Func<CancellationToken, Task<T>> operation, CancellationToken token) where T : class
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
            throw ResponseTimeout(side, ex);
        }
    }

    /// <inheritdoc cref="WithResponseTimeoutAsync{T}"/>
    public Task WithResponseTimeoutAsync(TransferSide side, Func<CancellationToken, Task> operation, CancellationToken token)
    {
        return WithResponseTimeoutAsync(side, async t =>
        {
            await operation(t).ConfigureAwait(false);
            return Done;
        }, token);
    }

    /// <summary>
    /// Turns any exception from a source or target into a transfer error, and decides which side it belongs to: the
    /// side a <see cref="TransferException"/> names, or else the side the engine was calling.
    /// </summary>
    public static (TransferException Error, TransferSide Side) Classify(Exception ex, TransferSide side)
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

    public static void CancelQuietly(CancellationTokenSource cts)
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

    // The result of an operation that returns nothing
    private static readonly object Done = new();

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
                _state.AddTransferred(-part.Consumed);
                part.Consumed = 0;
                part.Confirmed = 0;
                _state.ResetHash();
            }

            if (!failure.Retryable || !CanRetry(failure.Error, checkRepeatable: true) || attempts > _options.Retry.MaxRetries)
            {
                var range = part.Range.IsAll && _state.Info?.Length is long length ? new ByteRange(0, length) : part.Range;
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
        SourceRead? source = null;
        MeteredStream? metered = null;
        IdleWatchdog? watchdog = null;
        try
        {
            source = await WithResponseTimeoutAsync(TransferSide.Source, t => _source.OpenReadAsync(new ReadRequest(range, _state.Info?.Version), t), attempt.Token).ConfigureAwait(false);
            if (_state.Info == null)
            {
                _state.SetInfo(source.Info);
            }
            if (part.Range.IsAll)
            {
                part.ExpectedLength ??= _state.Info!.Length;
            }

            ITargetSession session;
            try
            {
                // With one connection the target opens once the first read described the content
                session = await _session(attempt.Token).ConfigureAwait(false);
            }
            catch (TransferFailedException ex) when (!token.IsCancellationRequested)
            {
                // Opening has its own retries, so the part isn't tried again
                return new AttemptFailure(ex.Error, ex.Side, resume: false, retryable: false);
            }

            // Bytes kept by earlier attempts stay kept; this attempt's bytes count once the target asks for more
            var keptBefore = part.Consumed;
            part.Confirmed = keptBefore;
            _state.StartHashIfNeeded(part);
            metered = new MeteredStream(source.Content, part.ExpectedLength - part.Consumed, _options.BufferSize, _limiters, bytes =>
            {
                _state.Hash(bytes.Span);
                part.Consumed += bytes.Length;
                _state.AddTransferred(bytes.Length);
                Telemetry.Bytes.Add(bytes.Length, _state.Tags);
            }, confirmed => part.Confirmed = keptBefore + confirmed, _state.Clock);

            // Disposing the source unblocks a read that ignores cancellation
            var opened = source;
            using var stopReads = attempt.Token.Register(() => opened.Dispose());
            watchdog = IdleWatchdog.Start(metered, _options.IdleTimeout, _options.ResponseTimeout, attempt, _state.Clock);

            await session.WritePartAsync(range, metered, attempt.Token).ConfigureAwait(false);
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
                var error = watchdog.WhileFinishing
                    ? new TransferException($"The target didn't finish the part within {_options.ResponseTimeout.TotalSeconds:0.###} seconds of receiving it",
                        TransferErrorKind.Timeout, isTransient: true, side, innerException: ex)
                    : new TransferException($"No data moved for {_options.IdleTimeout.TotalSeconds:0.###} seconds", TransferErrorKind.Timeout,
                        isTransient: true, side, innerException: ex);
                return new AttemptFailure(error, side, resume: side == TransferSide.Source && _state.CanResumePart(), retryable: CanRepeat(metered!));
            }
            if (metered?.ReadFailure is Exception readFailure)
            {
                // The source failed while the target was reading: the target kept what it read
                return Failed(readFailure, TransferSide.Source, resume: _state.CanResumePart());
            }
            if (metered == null)
            {
                // Nothing was written in this attempt, so earlier progress still holds
                return Failed(ex, source == null ? TransferSide.Source : TransferSide.Target, resume: _state.CanResumePart());
            }
            // The target failed on its own, so what it read may not have been kept
            var (targetError, observed) = Classify(ex, TransferSide.Target);
            return new AttemptFailure(targetError, observed, resume: false, retryable: CanRepeat(metered));
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

    private async Task WaitBeforeRetryAsync(TransferException error, TransferSide side, int retry, CancellationToken token)
    {
        _state.RetryStarted(error);
        try
        {
            var delay = _options.Retry.GetDelay(retry, error.RetryDelay);

            var tags = _state.Tags;
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
            _state.RetryEnded();
        }
    }

    /// <summary>
    /// Whether a part can be written again after the target failed on it. A target without
    /// <see cref="TargetCapabilities.RepeatableWrites"/> may already have processed a part it read completely, such as
    /// a <c>POST</c> whose answer was lost, so only a part it hadn't read completely is sent again.
    /// </summary>
    private bool CanRepeat(MeteredStream metered) => _capabilities.RepeatableWrites || !metered.ReachedEnd;

    private bool CanRetry(TransferException error, bool checkRepeatable)
    {
        return error.IsTransient &&
               error.Kind != TransferErrorKind.SourceChanged &&
               (!checkRepeatable || _state.Info?.IsRepeatable != false);
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
                if (t.Result is IAsyncDisposable asyncDisposable)
                {
                    _ = asyncDisposable.DisposeAsync().AsTask().ContinueWith(d => _ = d.Exception, TaskScheduler.Default);
                }
                else
                {
                    (t.Result as IDisposable)?.Dispose();
                }
            }
            else
            {
                _ = t.Exception;
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private TransferException ResponseTimeout(TransferSide side, Exception? inner)
    {
        var name = side == TransferSide.Target ? "target" : "source";
        return new TransferException($"The {name} didn't respond within {_options.ResponseTimeout.TotalSeconds:0.###} seconds", TransferErrorKind.Timeout, isTransient: true, side, innerException: inner);
    }

    private sealed class AttemptFailure
    {
        public AttemptFailure(TransferException error, TransferSide side, bool resume, bool retryable = true)
        {
            Error = error;
            Side = side;
            Resume = resume;
            Retryable = retryable;
        }

        public TransferException Error { get; }

        /// <summary>The side the engine saw the failure on.</summary>
        public TransferSide Side { get; }

        /// <summary>Whether the next attempt can continue after the bytes the target kept.</summary>
        public bool Resume { get; }

        /// <summary>
        /// Whether another attempt is allowed: false when the target can't take a part twice and may already have it,
        /// or when the failure already went through its own retries.
        /// </summary>
        public bool Retryable { get; }
    }
}
