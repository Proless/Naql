using CsCheck;
using Naqel.Core.Tests.Fakes;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Naqel.Core.Tests.PropertyTests;

/// <summary>
/// The engine's guarantees, checked against generated transfers: random content, connections, source and target
/// capabilities, retry budgets, faults on any attempt, connections changed while parts run, and parts split for
/// connections that free up. A failure prints the smallest case CsCheck could find and a seed to replay it with.
/// </summary>
public sealed class EnginePropertyTests
{
    private enum FaultKind
    {
        /// <summary>Opening the read fails, transiently.</summary>
        SourceOpenFails,

        /// <summary>The read breaks after some bytes, transiently.</summary>
        SourceBreaks,

        /// <summary>Opening the read is refused for good.</summary>
        SourceDenied,

        /// <summary>The write fails before reading anything, transiently.</summary>
        TargetFailsBeforeReading,

        /// <summary>The write fails after reading its whole part, like a lost answer to an upload.</summary>
        TargetFailsAfterReading
    }

    private sealed record Fault(FaultKind Kind, int Attempt, int Offset);

    private sealed record Scenario(
        int Length,
        int Connections,
        bool HasVersion,
        bool SupportsRanges,
        bool ParallelParts,
        bool SplittableParts,
        bool Interleaved,
        int? ConnectionsLater,
        int ChangeAtWrite,
        bool ResumableParts,
        bool Resumable,
        bool RepeatableWrites,
        bool WithChecksum,
        int MaxRetries,
        int? SourceMaxConnections,
        Fault[] Faults);

    private static readonly Gen<Fault> FaultGen =
        from kind in Gen.Enum<FaultKind>()
        from attempt in Gen.Int[0, 6]
        from offset in Gen.Int[0, 12_000]
        select new Fault(kind, attempt, offset);

    private static readonly Gen<Scenario> ScenarioGen =
        from length in Gen.Frequency((1, Gen.Int[0, 10]), (4, Gen.Int[0, 12_000]))
        from connections in Gen.Int[1, 6]
        from hasVersion in Gen.Frequency((4, Gen.Const(true)), (1, Gen.Const(false)))
        from supportsRanges in Gen.Frequency((4, Gen.Const(true)), (1, Gen.Const(false)))
        from parallelParts in Gen.Bool
        from splittableParts in Gen.Bool
        from interleaved in Gen.Bool
        from connectionsLater in Gen.Frequency((2, Gen.Const((int?)null)), (1, Gen.Int[1, 6].Select(count => (int?)count)))
        from changeAtWrite in Gen.Int[0, 3]
        from resumableParts in Gen.Bool
        from resumable in Gen.Bool
        from repeatableWrites in Gen.Frequency((3, Gen.Const(true)), (1, Gen.Const(false)))
        from withChecksum in Gen.Frequency((1, Gen.Const(true)), (3, Gen.Const(false)))
        from maxRetries in Gen.Int[0, 4]
        from sourceMaxConnections in Gen.Frequency((3, Gen.Const((int?)null)), (1, Gen.Int[1, 3].Select(limit => (int?)limit)))
        from faults in FaultGen.Array[0, 4]
        select new Scenario(length, connections, hasVersion, supportsRanges, parallelParts, splittableParts, interleaved, connectionsLater,
            changeAtWrite, resumableParts, resumable, repeatableWrites, withChecksum, maxRetries, sourceMaxConnections, faults);

    // The messages of the faults the test injects
    private static readonly string[] InjectedErrors = ["Denied", "The connection was refused", "The connection was reset", "The disk is busy", "The answer was lost"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public Task Every_transfer_commits_exactly_the_content_or_nothing()
    {
        return ScenarioGen.SampleAsync(RunAsync, iter: 300, threads: 1, print: Describe, writeLine: TestContext.Current.TestOutputHelper!.WriteLine);
    }

    private static async Task RunAsync(Scenario scenario)
    {
        var content = Content(scenario.Length);
        var source = Source(content, scenario);
        var faulty = new StrongBox<bool>(true);
        var running = new StrongBox<TransferOperation?>();
        var target = Target(scenario, faulty, running);
        var options = Options(scenario, content);
        var operation = new TransferClient(options).From(source).To(target).Build();
        running.Value = operation;

        TransferResult? result = null;
        TransferFailedException? failure = null;
        try
        {
            result = await operation.RunAsync(cancellationToken: Token);
        }
        catch (TransferFailedException ex)
        {
            failure = ex;
        }

        var committed = target.Sessions.Where(session => session.Completed).ToList();
        if (scenario.Faults.Length == 0)
        {
            Assert.NotNull(result);
        }
        if (result != null)
        {
            // Exactly one commit, of exactly the content
            var session = Assert.Single(committed);
            Assert.Equal(content, session.Data);
            Assert.Equal(content.Length, result.Transferred);
        }
        else
        {
            // A failed transfer never makes anything visible, and fails only because of a fault it was given, never
            // because the engine lost track of the bytes
            Assert.NotNull(failure);
            Assert.Empty(committed);
            Assert.NotEmpty(scenario.Faults);
            foreach (var error in failure.PartFailures.Select(part => part.Error).Append(failure.Error))
            {
                Assert.Contains(error.Message, InjectedErrors);
            }
        }

        // Never more writes at once than the options and the source allow; fewer connections let running parts finish
        var allowed = Math.Min(Math.Max(scenario.Connections, scenario.ConnectionsLater ?? 0), scenario.SourceMaxConnections ?? int.MaxValue);
        Assert.All(target.Sessions, session => Assert.True(session.MaxActiveWrites <= allowed, $"{session.MaxActiveWrites} writes at once, but only {allowed} allowed"));

        if (!scenario.RepeatableWrites)
        {
            // A target that can't take a part twice never gets one it already read completely
            foreach (var session in target.Sessions)
            {
                Assert.Equal(session.CompletedRanges.Count, session.CompletedRanges.Select(range => range.Start).Distinct().Count());
            }
        }

        if (failure != null && operation.GetCheckpoint() is { } checkpoint)
        {
            // The resumed run uses the same target, to find its partial data, but without the faults
            faulty.Value = false;
            await CheckCheckpointAsync(checkpoint, content, target, scenario);
        }
    }

    /// <summary>
    /// A checkpoint only counts bytes the target has, and continuing it without faults gives exactly the content.
    /// </summary>
    private static async Task CheckCheckpointAsync(TransferCheckpoint checkpoint, byte[] content, MemoryTarget target, Scenario scenario)
    {
        var kept = target.Sessions.Single(session => session.ResumeToken == checkpoint.ResumeToken).Data;
        foreach (var part in checkpoint.Parts)
        {
            for (var i = part.Range.Start; i < part.Range.Start + part.Completed; i++)
            {
                Assert.True(i < kept.Length && kept[i] == content[i], $"The checkpoint counts byte {i} of {part.Range}, which the target doesn't have");
            }
        }

        var calm = scenario with { Faults = [] };
        var result = await new TransferClient(Options(calm, content)).From(Source(content, calm)).To(target)
            .ResumeFrom(checkpoint).RunAsync(cancellationToken: Token);

        Assert.Equal(content.Length, result.Transferred);
        Assert.Equal(content, target.Sessions.Last(session => session.Completed).Data);
    }

    private static MemorySource Source(byte[] content, Scenario scenario)
    {
        Fault? At(int attempt, params FaultKind[] kinds) => scenario.Faults.FirstOrDefault(fault => fault.Attempt == attempt && kinds.Contains(fault.Kind));

        return new MemorySource(content)
        {
            Version = scenario.HasVersion ? "v1" : null,
            MaxConnections = scenario.SourceMaxConnections,
            SupportsRanges = scenario.SupportsRanges,
            OnOpen = (attempt, _, _) => At(attempt, FaultKind.SourceOpenFails, FaultKind.SourceDenied) switch
            {
                { Kind: FaultKind.SourceDenied } => throw new UnauthorizedAccessException("Denied"),
                { } => throw new IOException("The connection was refused"),
                _ => Task.CompletedTask
            },
            WrapContent = (attempt, _, stream) =>
            {
                if (At(attempt, FaultKind.SourceBreaks) is { } fault)
                {
                    stream = new FailingStream(stream, fault.Offset, () => new IOException("The connection was reset"));
                }
                return scenario.Interleaved ? new SlowStream(stream, TimeSpan.Zero) : stream;
            }
        };
    }

    private static MemoryTarget Target(Scenario scenario, StrongBox<bool> faulty, StrongBox<TransferOperation?> running)
    {
        bool Has(int write, FaultKind kind) => faulty.Value && scenario.Faults.Any(fault => fault.Attempt == write && fault.Kind == kind);

        return new MemoryTarget
        {
            Capabilities = new TargetCapabilities
            {
                ParallelParts = scenario.ParallelParts,
                SplittableParts = scenario.SplittableParts,
                ResumableParts = scenario.ResumableParts,
                Resumable = scenario.Resumable,
                RepeatableWrites = scenario.RepeatableWrites,
                Verifiable = true
            },
            OnWrite = (write, _, _) =>
            {
                // Like a person changing the setting while parts run, which may also split this very part
                if (faulty.Value && write == scenario.ChangeAtWrite && scenario.ConnectionsLater is int later)
                {
                    running.Value?.SetMaxConnections(later);
                }
                return Has(write, FaultKind.TargetFailsBeforeReading) ? throw new IOException("The disk is busy") : Task.CompletedTask;
            },
            AfterRead = (write, _) => Has(write, FaultKind.TargetFailsAfterReading) ? throw new IOException("The answer was lost") : Task.CompletedTask
        };
    }

    private static TransferOptions Options(Scenario scenario, byte[] content) => new()
    {
        MaxConnections = scenario.Connections,
        MinSplitPartSize = 1_000,
        BufferSize = 1_000,
        Retry = new RetryPolicy { MaxRetries = scenario.MaxRetries, InitialDelay = TimeSpan.Zero, Jitter = false },
        ResponseTimeout = TimeSpan.FromSeconds(10),
        IdleTimeout = TimeSpan.FromSeconds(10),
        ExpectedChecksum = scenario.WithChecksum ? new Checksum(HashAlgorithmName.SHA256, SHA256.HashData(content)) : null
    };

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        new Random(length).NextBytes(content);
        return content;
    }

    private static string Describe(Scenario scenario)
    {
        var faults = scenario.Faults.Length == 0 ? "none" : string.Join(", ", scenario.Faults.Select(fault => $"{fault.Kind} on attempt {fault.Attempt} at {fault.Offset}"));
        return $"{scenario with { Faults = [] }} faults: {faults}";
    }
}
