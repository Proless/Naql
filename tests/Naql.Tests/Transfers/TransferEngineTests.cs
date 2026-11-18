using Naql.Tests.Fakes;

namespace Naql.Tests.Transfers;

/// <summary>
/// The engine's behavior, driven with in-memory sources and targets.
/// </summary>
public sealed class TransferEngineTests
{
    private static readonly TransferOptions Options = new()
    {
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false },
        MinPartSize = 1_000,
        BufferSize = 1_000,
        ResponseTimeout = TimeSpan.FromSeconds(10),
        IdleTimeout = TimeSpan.FromSeconds(10),
        ProgressInterval = TimeSpan.FromMilliseconds(10)
    };

    private static readonly TransferOptions Parallel = Options with { MaxConnections = 4 };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        new Random(42).NextBytes(content);
        return content;
    }

    private static Task<TransferResult> RunAsync(ITransferSource source, ITransferTarget target, TransferOptions? options = null,
        IProgress<TransferProgress>? progress = null, CancellationToken? cancellationToken = null)
    {
        return new TransferClient(options ?? Options).RunAsync(source, target, progress: progress, cancellationToken: cancellationToken ?? Token);
    }

    private static Exception Broken() => new IOException("The connection was reset");

    #region Planning

    [Fact]
    public async Task One_connection_reads_the_whole_content_once_without_asking_for_info()
    {
        var content = Content(10_000);
        var source = new MemorySource(content);
        var target = new MemoryTarget();

        var result = await RunAsync(source, target);

        Assert.Equal(content, target.Session.Data);
        Assert.True(target.Session.Completed);
        Assert.True(target.Session.Disposed);
        Assert.False(target.Session.Aborted);
        Assert.Equal(0, source.InfoCalls);
        Assert.Equal(ByteRange.All, Assert.Single(source.Reads).Range);
        Assert.Equal(10_000, target.Session.Request.Source.Length);
        Assert.Equal(10_000, result.Transferred);
        Assert.Equal(1, result.Parts);
        Assert.Equal(0, result.Retries);
        Assert.Equal("v1", result.Source.Version);
    }

    [Fact]
    public async Task Parallel_parts_run_at_the_same_time_and_cover_the_content()
    {
        var content = Content(10_000);
        var source = new MemorySource(content);
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new MemoryTarget
        {
            OnWrite = async (_, _, ct) =>
            {
                if (Interlocked.Increment(ref started) == 4)
                {
                    allStarted.TrySetResult();
                }
                // Only completes when all four parts are being written at once
                await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
        };

        var result = await RunAsync(source, target, Parallel);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(4, result.Parts);
        Assert.Equal(4, target.Session.MaxActiveWrites);
        Assert.Equal(1, source.InfoCalls);
        Assert.Equal(
            new ByteRange[] { new(0, 2_500), new(2_500, 2_500), new(5_000, 2_500), new(7_500, 2_500) },
            target.Session.Writes.OrderBy(range => range.Start));
        Assert.All(source.Reads, read => Assert.Equal("v1", read.IfVersion));
    }

    [Fact]
    public async Task Content_without_a_version_isnt_split_into_parts()
    {
        var content = Content(10_000);
        var target = new MemoryTarget();

        // Parts read at different times could come from different versions of the content
        var result = await RunAsync(new MemorySource(content) { Version = null }, target, Parallel);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Parts);
    }

    [Fact]
    public async Task Content_without_a_version_is_split_when_a_checksum_will_catch_a_splice()
    {
        var content = Content(10_000);
        var checksum = new Checksum(System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.SHA256.HashData(content));
        var target = new MemoryTarget { Capabilities = new() { ParallelParts = true, ResumableParts = true, Resumable = true, Verifiable = true } };

        var result = await RunAsync(new MemorySource(content) { Version = null }, target, Parallel with { ExpectedChecksum = checksum });

        Assert.Equal(4, result.Parts);
        Assert.Equal(checksum, result.VerifiedChecksum);
    }

    [Fact]
    public async Task A_part_of_content_without_a_version_starts_over_instead_of_continuing()
    {
        var content = Content(10_000);
        var source = new MemorySource(content)
        {
            Version = null,
            WrapContent = (index, _, stream) => index == 0 ? new FailingStream(stream, 4_000, Broken) : stream
        };
        var target = new MemoryTarget();

        await RunAsync(source, target);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(new ByteRange[] { ByteRange.All, ByteRange.All }, source.Reads.Select(read => read.Range));
    }

    [Fact]
    public async Task Uses_one_connection_when_the_source_has_no_ranges()
    {
        var content = Content(10_000);
        var source = new MemorySource(content) { SupportsRanges = false };
        var target = new MemoryTarget();

        var result = await RunAsync(source, target, Parallel);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Parts);
        Assert.Equal(ByteRange.All, Assert.Single(source.Reads).Range);
    }

    [Fact]
    public async Task Uses_one_connection_when_the_target_takes_no_parallel_parts()
    {
        var content = Content(10_000);
        var source = new MemorySource(content);
        var target = new MemoryTarget { Capabilities = new() { ResumableParts = true } };

        var result = await RunAsync(source, target, Parallel);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Parts);
        Assert.Equal(0, source.InfoCalls);
    }

    [Fact]
    public async Task Copies_content_of_unknown_length()
    {
        var content = Content(10_000);
        var target = new MemoryTarget();

        var result = await RunAsync(new MemorySource(content) { ReportsLength = false }, target, Parallel);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(10_000, result.Transferred);
        Assert.Null(target.Session.Request.Source.Length);
    }

    [Fact]
    public async Task Fails_early_when_the_target_needs_a_length_the_source_doesnt_report()
    {
        var target = new MemoryTarget { Capabilities = new() { RequiresLength = true } };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(new MemorySource(Content(100)) { ReportsLength = false }, target));

        Assert.Equal(TransferErrorKind.Unsupported, ex.Kind);
        Assert.Equal(TransferSide.Engine, ex.Side);
        Assert.Empty(target.Sessions);
    }

    #endregion

    #region Errors

    [Fact]
    public async Task A_failure_outside_the_parts_has_no_part_failures()
    {
        var source = new MemorySource(Content(10_000)) { OnGetInfo = (_, _) => throw new TransferException("Denied", TransferErrorKind.AccessDenied) };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, new MemoryTarget(), Parallel));

        Assert.Equal(TransferErrorKind.AccessDenied, ex.Kind);
        Assert.Equal(TransferSide.Source, ex.Side);
        Assert.Empty(ex.PartFailures);
    }

    [Fact]
    public async Task A_timed_out_task_is_retried_as_a_timeout()
    {
        var source = new MemorySource(Content(1_000)) { OnOpen = (index, _, _) => index == 0 ? throw new TaskCanceledException("HttpClient.Timeout") : Task.CompletedTask };

        var result = await RunAsync(source, new MemoryTarget());

        Assert.Equal(1, result.Retries);
    }

    [Fact]
    public async Task A_cancellation_the_caller_didnt_ask_for_isnt_retried()
    {
        var source = new MemorySource(Content(1_000)) { OnOpen = (_, _, _) => throw new OperationCanceledException("Someone else's token") };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, new MemoryTarget()));

        Assert.Equal(TransferErrorKind.Unknown, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.IsType<OperationCanceledException>(ex.Error.InnerException);
        Assert.Single(source.Reads);
    }

    #endregion

    #region Retries and resuming

    [Fact]
    public async Task Continues_a_part_where_the_source_failed_when_the_target_keeps_partial_parts()
    {
        var content = Content(10_000);
        var source = new MemorySource(content)
        {
            WrapContent = (index, _, stream) => index == 0 ? new FailingStream(stream, 4_000, Broken) : stream
        };
        var target = new MemoryTarget();

        var result = await RunAsync(source, target);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Retries);
        Assert.Equal(new ByteRange[] { ByteRange.All, new(4_000) }, source.Reads.Select(read => read.Range));
        Assert.Equal("v1", source.Reads[1].IfVersion);
    }

    [Fact]
    public async Task Continues_a_parallel_part_where_the_source_failed()
    {
        var content = Content(10_000);
        var source = new MemorySource(content)
        {
            WrapContent = (_, request, stream) => request.Range == new ByteRange(5_000, 2_500) ? new FailingStream(stream, 1_000, Broken) : stream
        };
        var target = new MemoryTarget();

        await RunAsync(source, target, Parallel);

        Assert.Equal(content, target.Session.Data);
        Assert.Contains(source.Reads, read => read.Range == new ByteRange(6_000, 1_500));
    }

    [Fact]
    public async Task Starts_a_part_over_when_the_target_cant_keep_partial_parts()
    {
        var content = Content(10_000);
        var source = new MemorySource(content)
        {
            WrapContent = (index, _, stream) => index == 0 ? new FailingStream(stream, 4_000, Broken) : stream
        };
        var target = new MemoryTarget { Capabilities = new() };

        await RunAsync(source, target);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(new ByteRange[] { ByteRange.All, ByteRange.All }, source.Reads.Select(read => read.Range));
    }

    [Fact]
    public async Task Starts_a_part_over_when_the_target_itself_failed()
    {
        var content = Content(10_000);
        var source = new MemorySource(content);
        var target = new MemoryTarget { FailFirstWriteAfter = 3_000 };

        var result = await RunAsync(source, target);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Retries);
        Assert.Equal(new ByteRange[] { ByteRange.All, ByteRange.All }, source.Reads.Select(read => read.Range));
    }

    [Fact]
    public async Task Retries_when_the_source_ends_early()
    {
        var content = Content(10_000);
        var source = new MemorySource(content)
        {
            WrapContent = (index, _, stream) => index == 0 ? new MemoryStream(content, 0, 3_000) : stream
        };
        var target = new MemoryTarget();

        await RunAsync(source, target);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(new ByteRange(3_000), source.Reads[1].Range);
    }

    [Fact]
    public async Task Retries_getting_the_info()
    {
        var content = Content(10_000);
        var source = new MemorySource(content)
        {
            OnGetInfo = (call, _) => call == 0 ? throw Broken() : Task.CompletedTask
        };
        var target = new MemoryTarget();

        var result = await RunAsync(source, target, Parallel);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(2, source.InfoCalls);
        Assert.Equal(1, result.Retries);
    }

    [Fact]
    public async Task Does_not_retry_a_permanent_error()
    {
        var gone = new TransferException("Gone", TransferErrorKind.NotFound);
        var source = new MemorySource(Content(100)) { OnOpen = (_, _, _) => throw gone };
        var target = new MemoryTarget();

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, target));

        Assert.Equal(TransferErrorKind.NotFound, ex.Kind);
        // The engine knows it was reading the source; the source's own exception reaches the caller unchanged
        Assert.Equal(TransferSide.Source, ex.Side);
        Assert.Same(gone, ex.Error);
        Assert.Same(gone, ex.InnerException);
        Assert.Equal(TransferSide.Unknown, gone.Side);
        var part = Assert.Single(ex.PartFailures);
        Assert.Equal(ByteRange.All, part.Part);
        Assert.Equal(1, part.Attempts);
        Assert.Single(source.Reads);
        Assert.Empty(target.Sessions);
    }

    [Fact]
    public async Task Gives_up_after_the_configured_retries()
    {
        var source = new MemorySource(Content(100)) { OnOpen = (_, _, _) => throw Broken() };
        var options = Options with { Retry = Options.Retry with { MaxRetries = 2 } };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, new MemoryTarget(), options));

        Assert.Equal(TransferErrorKind.Network, ex.Kind);
        Assert.True(ex.IsTransient);
        Assert.IsType<IOException>(ex.Error.InnerException);
        Assert.Equal(3, Assert.Single(ex.PartFailures).Attempts);
        Assert.Equal(3, source.Reads.Count);
    }

    [Fact]
    public async Task Does_not_retry_a_source_that_cant_be_read_again()
    {
        var source = new MemorySource(Content(10_000))
        {
            IsRepeatable = false,
            WrapContent = (_, _, stream) => new FailingStream(stream, 1_000, Broken)
        };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, new MemoryTarget()));

        Assert.Equal(TransferErrorKind.Network, ex.Kind);
        Assert.Single(source.Reads);
    }

    [Fact]
    public async Task A_failed_part_stops_the_others_and_is_reported()
    {
        var failing = new ByteRange(2_500, 2_500);
        var source = new MemorySource(Content(10_000))
        {
            OnOpen = (_, request, _) => request.Range == failing ? throw new TransferException("Denied", TransferErrorKind.AccessDenied) : Task.CompletedTask
        };
        // The other parts wait until they are canceled
        var target = new MemoryTarget { OnWrite = (_, _, ct) => Task.Delay(Timeout.Infinite, ct) };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, target, Parallel));

        Assert.Equal(TransferErrorKind.AccessDenied, ex.Kind);
        var part = Assert.Single(ex.PartFailures);
        Assert.Equal(failing, part.Part);
        Assert.Same(ex.Error, part.Error);
        Assert.False(target.Session.Completed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Opening_the_target_is_retried_on_its_own(int connections)
    {
        var content = Content(10_000);
        var source = new MemorySource(content);
        var target = new MemoryTarget { OnOpen = (open, _) => open == 0 ? throw Broken() : Task.CompletedTask };

        var result = await RunAsync(source, target, Options with { MaxConnections = connections });

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Retries);
        // The source isn't read again just because the target failed to open
        Assert.Equal(result.Parts, source.Reads.Count);
    }

    [Fact]
    public async Task A_part_is_written_again_after_the_target_failed_once_it_had_all_of_it()
    {
        var content = Content(10_000);
        var target = new MemoryTarget { AfterRead = (write, _) => write == 0 ? throw Broken() : Task.CompletedTask };

        var result = await RunAsync(new MemorySource(content), target);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(2, target.Session.Writes.Count);
        Assert.Equal(1, result.Retries);
    }

    [Fact]
    public async Task A_target_that_cant_take_a_part_twice_doesnt_get_it_again_once_it_had_all_of_it()
    {
        var target = new MemoryTarget
        {
            Capabilities = new TargetCapabilities { RepeatableWrites = false },
            AfterRead = (write, _) => write == 0 ? throw Broken() : Task.CompletedTask
        };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(new MemorySource(Content(10_000)), target));

        // The error is still transient, so the caller can decide to send it again
        Assert.True(ex.IsTransient);
        Assert.Equal(TransferSide.Target, ex.Side);
        Assert.Equal(1, Assert.Single(ex.PartFailures).Attempts);
        Assert.Single(target.Session.Writes);
    }

    [Fact]
    public async Task A_target_that_cant_take_a_part_twice_gets_it_again_when_it_failed_before_having_all_of_it()
    {
        var content = Content(10_000);
        var target = new MemoryTarget { Capabilities = new TargetCapabilities { RepeatableWrites = false }, FailFirstWriteAfter = 2_000 };

        var result = await RunAsync(new MemorySource(content), target);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(2, target.Session.Writes.Count);
        Assert.Equal(1, result.Retries);
    }

    #endregion

    #region Source changes

    [Fact]
    public async Task Starts_over_once_when_the_source_changes()
    {
        var content = Content(10_000);
        MemorySource source = null!;
        source = new MemorySource(content)
        {
            WrapContent = (index, _, stream) =>
            {
                if (index != 0)
                {
                    return stream;
                }
                // The content changes while the first read is running, and the read breaks off
                source.Version = "v2";
                return new FailingStream(stream, 2_000, Broken);
            }
        };
        var target = new MemoryTarget();

        var result = await RunAsync(source, target);

        Assert.Equal(2, target.Sessions.Count);
        Assert.True(target.Sessions[0].Aborted);
        Assert.True(target.Sessions[1].Completed);
        Assert.Equal(content, target.Sessions[1].Data);
        Assert.Equal("v2", result.Source.Version);
        Assert.Equal(10_000, result.Transferred);
    }

    [Fact]
    public async Task Fails_when_the_source_changes_again_after_starting_over()
    {
        MemorySource source = null!;
        source = new MemorySource(Content(10_000))
        {
            WrapContent = (index, _, stream) =>
            {
                source.Version = $"v{index + 2}";
                return new FailingStream(stream, 2_000, Broken);
            }
        };
        var target = new MemoryTarget();

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, target));

        Assert.Equal(TransferErrorKind.SourceChanged, ex.Kind);
        Assert.Equal(2, target.Sessions.Count);
        Assert.All(target.Sessions, session => Assert.False(session.Completed));
    }

    #endregion

    #region Timeouts

    [Fact]
    public async Task Times_out_a_source_that_doesnt_respond_even_when_it_ignores_cancellation()
    {
        var content = Content(1_000);
        var source = new MemorySource(content)
        {
            OnOpen = (index, _, _) => index == 0 ? Task.Delay(Timeout.Infinite, CancellationToken.None) : Task.CompletedTask
        };
        var target = new MemoryTarget();

        var result = await RunAsync(source, target, Options with { ResponseTimeout = TimeSpan.FromMilliseconds(200) });

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Retries);
    }

    [Fact]
    public async Task Reports_a_response_timeout_as_a_transient_source_error()
    {
        var source = new MemorySource(Content(1_000)) { OnOpen = (_, _, ct) => Task.Delay(Timeout.Infinite, ct) };
        var options = Options with { ResponseTimeout = TimeSpan.FromMilliseconds(100), Retry = RetryPolicy.None };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, new MemoryTarget(), options));

        Assert.Equal(TransferErrorKind.Timeout, ex.Kind);
        Assert.Equal(TransferSide.Source, ex.Side);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task A_stalled_source_times_out_and_the_part_continues_where_it_stopped()
    {
        var content = Content(10_000);
        var source = new MemorySource(content)
        {
            // Stalls without honoring cancellation: only disposing the stream ends the read
            WrapContent = (index, _, stream) => index == 0 ? new StallingStream(stream, 3_000, honorCancellation: false) : stream
        };
        var target = new MemoryTarget();

        var result = await RunAsync(source, target, Options with { IdleTimeout = TimeSpan.FromMilliseconds(200) });

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Retries);
        Assert.Equal(new ByteRange(3_000), source.Reads[1].Range);
    }

    [Fact]
    public async Task A_stalled_target_times_out_as_a_target_error()
    {
        var target = new MemoryTarget { OnWrite = (_, _, ct) => Task.Delay(Timeout.Infinite, ct) };
        var options = Options with { IdleTimeout = TimeSpan.FromMilliseconds(200), Retry = RetryPolicy.None };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(new MemorySource(Content(1_000)), target, options));

        Assert.Equal(TransferErrorKind.Timeout, ex.Kind);
        Assert.Equal(TransferSide.Target, ex.Side);
    }

    [Fact]
    public async Task Waiting_for_the_speed_limit_is_not_idle()
    {
        var content = Content(20_000);
        var target = new MemoryTarget();
        // 20 KB at 50 KB/s takes about 0.4 s, longer than the idle timeout
        var options = Options with { MaxBytesPerSecond = 50_000, BufferSize = 10_000, IdleTimeout = TimeSpan.FromMilliseconds(150), Retry = RetryPolicy.None };

        await RunAsync(new MemorySource(content), target, options);

        Assert.Equal(content, target.Session.Data);
    }

    [Fact]
    public async Task Finishing_a_part_the_target_has_read_completely_is_not_idle()
    {
        var content = Content(1_000);
        // Like a server that takes a while to answer an upload it has completely
        var target = new MemoryTarget { AfterRead = (_, ct) => Task.Delay(400, ct) };
        var options = Options with { IdleTimeout = TimeSpan.FromMilliseconds(100), Retry = RetryPolicy.None };

        await RunAsync(new MemorySource(content), target, options);

        Assert.Equal(content, target.Session.Data);
    }

    [Fact]
    public async Task A_target_that_doesnt_finish_a_part_it_has_read_completely_times_out_after_the_response_timeout()
    {
        var target = new MemoryTarget { AfterRead = (_, ct) => Task.Delay(Timeout.Infinite, ct) };
        var options = Options with { ResponseTimeout = TimeSpan.FromMilliseconds(200), Retry = RetryPolicy.None };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(new MemorySource(Content(1_000)), target, options));

        Assert.Equal(TransferErrorKind.Timeout, ex.Kind);
        Assert.Equal(TransferSide.Target, ex.Side);
        Assert.Contains("didn't finish", ex.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Times_out_a_target_that_doesnt_open_even_when_it_ignores_cancellation(int connections)
    {
        var content = Content(10_000);
        var target = new MemoryTarget { OnOpen = (open, _) => open == 0 ? Task.Delay(Timeout.Infinite, CancellationToken.None) : Task.CompletedTask };
        var options = Options with { MaxConnections = connections, ResponseTimeout = TimeSpan.FromMilliseconds(200) };

        var result = await RunAsync(new MemorySource(content), target, options);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Retries);
    }

    [Fact]
    public async Task A_target_that_doesnt_complete_times_out_as_a_target_error()
    {
        var target = new MemoryTarget { OnComplete = _ => Task.Delay(Timeout.Infinite, CancellationToken.None) };
        var options = Options with { ResponseTimeout = TimeSpan.FromMilliseconds(200), Retry = RetryPolicy.None };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(new MemorySource(Content(1_000)), target, options));

        Assert.Equal(TransferErrorKind.Timeout, ex.Kind);
        Assert.Equal(TransferSide.Target, ex.Side);
        Assert.Contains("target didn't respond", ex.Message);
    }

    [Fact]
    public async Task Cleaning_up_after_a_failure_doesnt_wait_forever_for_the_target()
    {
        var target = new MemoryTarget
        {
            // A target that isn't resumable is aborted on failure
            Capabilities = new TargetCapabilities(),
            OnAbort = _ => Task.Delay(Timeout.Infinite, CancellationToken.None)
        };
        var source = new MemorySource(Content(1_000)) { WrapContent = (_, _, stream) => new FailingStream(stream, 500, () => new UnauthorizedAccessException("Denied")) };
        var options = Options with { ResponseTimeout = TimeSpan.FromMilliseconds(200) };

        var run = RunAsync(source, target, options);

        await Assert.ThrowsAsync<TransferFailedException>(() => run.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.True(target.Session.Aborted);
    }

    #endregion

    #region Cancellation and cleanup

    [Fact]
    public async Task Canceling_keeps_what_was_written_when_the_target_can_resume()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var target = new MemoryTarget();

        var ex = await CancelWhileStalledAsync(target, cts);

        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.False(target.Session.Completed);
        Assert.False(target.Session.Aborted);
        Assert.True(target.Session.Disposed);
    }

    [Fact]
    public async Task Canceling_discards_what_was_written_when_the_target_cant_resume()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var target = new MemoryTarget { Capabilities = new() { ResumableParts = true } };

        await CancelWhileStalledAsync(target, cts);

        Assert.True(target.Session.Aborted);
        Assert.True(target.Session.Disposed);
    }

    [Fact]
    public async Task Canceling_while_waiting_to_retry_ends_at_once()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var source = new MemorySource(Content(1_000))
        {
            OnOpen = (_, _, _) =>
            {
                cts.CancelAfter(TimeSpan.FromMilliseconds(100));
                throw Broken();
            }
        };
        var options = Options with { Retry = Options.Retry with { InitialDelay = TimeSpan.FromMinutes(1) } };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(source, new MemoryTarget(), options, cancellationToken: cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed.TotalSeconds} s");
        Assert.Single(source.Reads);
    }

    [Fact]
    public async Task A_failure_discards_what_was_written_when_asked_to()
    {
        var source = new MemorySource(Content(10_000)) { WrapContent = (_, _, stream) => new FailingStream(stream, 1_000, Broken) };
        var target = new MemoryTarget();
        var options = Options with { DiscardPartialOnFailure = true, Retry = RetryPolicy.None };

        await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, target, options));

        Assert.True(target.Session.Aborted);
    }

    private static async Task<OperationCanceledException> CancelWhileStalledAsync(MemoryTarget target, CancellationTokenSource cts)
    {
        StallingStream? stalling = null;
        var source = new MemorySource(Content(10_000))
        {
            WrapContent = (_, _, stream) => stalling = new StallingStream(stream, 3_000, honorCancellation: true)
        };

        var run = RunAsync(source, target, cancellationToken: cts.Token);
        while (stalling == null)
        {
            await Task.Delay(10, Token);
        }
        await stalling.Stalled.WaitAsync(TimeSpan.FromSeconds(5), Token);
        cts.Cancel();

        return await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    #endregion

    #region Integrity

    [Fact]
    public async Task Fails_when_the_target_stops_reading_a_part_early()
    {
        var source = new MemorySource(Content(10_000));
        var target = new MemoryTarget { StopReadingAfter = 1_000 };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, target));

        Assert.Equal(TransferErrorKind.Integrity, ex.Kind);
        Assert.Equal(TransferSide.Target, ex.Side);
        Assert.Single(source.Reads);
        Assert.False(target.Session.Completed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task A_target_that_claims_to_be_verifiable_but_isnt_fails_instead_of_skipping_the_check(int connections)
    {
        var inner = new MemoryTarget();
        var target = new UnverifiableTarget(inner);
        var options = Options with { MaxConnections = connections, ExpectedChecksum = Checksum.Sha256(new string('0', 64)) };

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(new MemorySource(Content(10_000)), target, options));

        Assert.Equal(TransferErrorKind.Unsupported, ex.Kind);
        Assert.Equal(TransferSide.Target, ex.Side);
        Assert.Contains("IVerifiableSession", ex.Message);
        Assert.True(inner.Session.Disposed);
        Assert.Empty(inner.Session.Writes);
    }

    [Fact]
    public async Task The_result_has_the_sessions_location_and_the_session_is_disposed()
    {
        var target = new MemoryTarget();

        var result = await RunAsync(new MemorySource(Content(1_000)), target);

        Assert.Equal("memory://memory-0", result.Location);
        Assert.True(target.Session.Disposed);
    }

    /// <summary>
    /// Says it's verifiable, but its sessions can't read back what they wrote.
    /// </summary>
    private sealed class UnverifiableTarget(MemoryTarget inner) : ITransferTarget
    {
        public TargetCapabilities Capabilities { get; } = inner.Capabilities with { Verifiable = true };

        public async Task<ITargetSession> OpenAsync(TargetOpenRequest request, CancellationToken cancellationToken)
        {
            return new PlainSession(await inner.OpenAsync(request, cancellationToken));
        }

        private sealed class PlainSession(ITargetSession session) : ITargetSession
        {
            public string? Location => session.Location;
            public string? ResumeToken => session.ResumeToken;
            public Task WritePartAsync(ByteRange range, Stream content, CancellationToken cancellationToken) => session.WritePartAsync(range, content, cancellationToken);
            public Task CompleteAsync(CancellationToken cancellationToken) => session.CompleteAsync(cancellationToken);
            public Task AbortAsync(CancellationToken cancellationToken) => session.AbortAsync(cancellationToken);
            public ValueTask DisposeAsync() => session.DisposeAsync();
        }
    }

    #endregion

    #region Progress and throttling

    [Fact]
    public async Task Progress_ends_with_the_completed_total()
    {
        var reports = new List<TransferProgress>();
        var progress = new SynchronousProgress(report =>
        {
            lock (reports)
            {
                reports.Add(report);
            }
        });

        await RunAsync(new MemorySource(Content(10_000)), new MemoryTarget(), Parallel, progress);

        var last = reports[^1];
        Assert.Equal(TransferState.Completed, last.State);
        Assert.Equal(10_000, last.Transferred);
        Assert.Equal(10_000, last.Total);
        Assert.Equal(1, last.Fraction);
        Assert.Equal(4, last.TotalParts);
        Assert.Equal(4, last.CompletedParts);
        Assert.Equal(0, last.ActiveParts);
    }

    [Fact]
    public async Task Progress_shows_retries_with_the_error()
    {
        var reports = new List<TransferProgress>();
        var progress = new SynchronousProgress(report =>
        {
            lock (reports)
            {
                reports.Add(report);
            }
        });
        var source = new MemorySource(Content(1_000)) { OnOpen = (index, _, _) => index == 0 ? throw Broken() : Task.CompletedTask };
        var options = Options with { Retry = Options.Retry with { InitialDelay = TimeSpan.FromMilliseconds(200) } };

        await RunAsync(source, new MemoryTarget(), options, progress);

        var retrying = Assert.Single(reports.Where(report => report.State == TransferState.Retrying).Take(1));
        var error = Assert.IsType<TransferException>(retrying.LastError);
        Assert.Equal(TransferErrorKind.Network, error.Kind);
    }

    [Fact]
    public async Task An_exception_from_the_progress_handler_ends_the_transfer_unchanged()
    {
        var bug = new InvalidOperationException("A bug in the progress handler");
        var source = new MemorySource(Content(1_000)) { OnOpen = (_, _, ct) => Task.Delay(500, ct) };
        var target = new MemoryTarget();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(source, target, progress: new SynchronousProgress(_ => throw bug)));

        Assert.Same(bug, ex);
        Assert.Single(source.Reads);
    }

    [Fact]
    public async Task The_speed_limit_applies_to_the_whole_transfer()
    {
        var content = Content(200_000);
        var target = new MemoryTarget();
        var options = Parallel with { MinPartSize = 10_000, MaxBytesPerSecond = 400_000 };

        var result = await RunAsync(new MemorySource(content), target, options);

        // 200 KB at 400 KB/s, minus the 40 KB the limiter lets through at once, takes at least 0.4 s
        Assert.Equal(content, target.Session.Data);
        Assert.True(result.Duration >= TimeSpan.FromMilliseconds(350), $"Took only {result.Duration.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task A_shared_limiter_limits_transfers_together()
    {
        var client = new TransferClient(Options, new BandwidthLimiter(400_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Task.WhenAll(
            client.RunAsync(new MemorySource(Content(100_000)), new MemoryTarget(), cancellationToken: Token),
            client.RunAsync(new MemorySource(Content(100_000)), new MemoryTarget(), cancellationToken: Token));

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(350), $"Took only {watch.Elapsed.TotalMilliseconds} ms");
    }

    /// <summary>
    /// Calls the handler on the reporting thread, unlike <see cref="Progress{T}"/>.
    /// </summary>
    private sealed class SynchronousProgress(Action<TransferProgress> handler) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => handler(value);
    }

    #endregion
}
