using CsCheck;
using Naqel.Core.Tests.Fakes;

namespace Naqel.Core.Tests.PropertyTests;

/// <summary>
/// Transfers canceled at random moments: whatever was running when the cancellation came, the transfer ends promptly
/// as canceled or completed, cleans up every stream and session, and leaves a checkpoint that only counts kept bytes.
/// </summary>
public sealed class CancellationPropertyTests
{
    private sealed record Scenario(int Length, int Connections, int CancelAfterMs, bool Resumable, bool SourceBreaks, int BreakAt);

    private static readonly Gen<Scenario> ScenarioGen =
        from length in Gen.Int[0, 20_000]
        from connections in Gen.Int[1, 6]
        from cancelAfter in Gen.Int[0, 150]
        from resumable in Gen.Bool
        from sourceBreaks in Gen.Bool
        from breakAt in Gen.Int[0, 20_000]
        select new Scenario(length, connections, cancelAfter, resumable, sourceBreaks, breakAt);

    [Fact]
    public Task A_canceled_transfer_ends_promptly_and_cleans_up()
    {
        return ScenarioGen.SampleAsync(RunAsync, iter: 100, threads: 1, writeLine: TestContext.Current.TestOutputHelper!.WriteLine);
    }

    /// <summary>
    /// Waits a millisecond or so before each read, and honors cancellation while it waits.
    /// </summary>
    private sealed class SlowStream(Stream inner) : ReadOnlyStream
    {
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(1, cancellationToken);
            return await inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static async Task RunAsync(Scenario scenario)
    {
        var content = new byte[scenario.Length];
        new Random(scenario.Length).NextBytes(content);
        using var leaks = new Leaks();
        var source = new MemorySource(content)
        {
            // Every read takes a moment, so the cancellation lands in the middle of the work
            WrapContent = (attempt, _, stream) => leaks.Track(new SlowStream(scenario.SourceBreaks && attempt == 0
                ? new FailingStream(stream, scenario.BreakAt, () => new IOException("The connection was reset"))
                : stream))
        };
        var target = new MemoryTarget { Capabilities = new() { ParallelParts = true, ResumableParts = true, Resumable = scenario.Resumable } };
        var options = new TransferOptions
        {
            MaxConnections = scenario.Connections,
            MinSplitPartSize = 1_000,
            BufferSize = 1_000,
            Retry = new RetryPolicy { MaxRetries = 2, InitialDelay = TimeSpan.FromMilliseconds(5), Jitter = false }
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(scenario.CancelAfterMs);
        var operation = new TransferClient(options).From(source).To(target).Build();

        var run = operation.RunAsync(cancellationToken: cts.Token);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(finished == run, "The transfer didn't end within 10 seconds of being canceled");

        var completed = false;
        try
        {
            await run;
            completed = true;
        }
        catch (OperationCanceledException ex)
        {
            // The caller's cancellation, reported as such, never as a failure
            Assert.Equal(cts.Token, ex.CancellationToken);
        }

        Assert.All(target.Sessions, session => Assert.True(session.Disposed, "A session wasn't disposed"));
        Assert.Equal(0, leaks.OpenStreams);
        if (completed)
        {
            Assert.Equal(content, Assert.Single(target.Sessions, session => session.Completed).Data);
        }
        else
        {
            Assert.DoesNotContain(target.Sessions, session => session.Completed);
            if (operation.GetCheckpoint() is { } checkpoint)
            {
                var kept = target.Sessions.Single(session => session.ResumeToken == checkpoint.ResumeToken).Data;
                foreach (var part in checkpoint.Parts)
                {
                    for (var i = part.Range.Start; i < part.Range.Start + part.Completed; i++)
                    {
                        Assert.True(i < kept.Length && kept[i] == content[i], $"The checkpoint counts byte {i}, which the target doesn't have");
                    }
                }
            }
        }
        Assert.Empty(leaks.Unobserved());
    }
}
