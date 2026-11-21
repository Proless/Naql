using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using CsCheck;
using Naqel.Core.Tests.Fakes;

namespace Naqel.Core.Tests.PropertyTests;

/// <summary>
/// Uploads to a target that keeps only what its server acknowledged, checked against generated chunk sizes, retry
/// budgets and faults: the server never gets a chunk at the wrong offset, a completed upload is exactly the content,
/// and a checkpoint counts only what the server acknowledged and continues to exactly the content.
/// </summary>
public sealed class AcknowledgingPropertyTests
{
    private sealed record Scenario(int Length, int ChunkSize, bool HasVersion, bool Verifiable, bool WithChecksum, int MaxRetries,
        int[] FailedSends, (int Read, int Offset)[] SourceBreaks);

    private static readonly Gen<Scenario> ScenarioGen =
        from length in Gen.Int[0, 12_000]
        from chunkSize in Gen.Int[300, 5_000]
        from hasVersion in Gen.Frequency((4, Gen.Const(true)), (1, Gen.Const(false)))
        from verifiable in Gen.Bool
        from withChecksum in Gen.Frequency((1, Gen.Const(true)), (2, Gen.Const(false)))
        from maxRetries in Gen.Int[0, 4]
        from failedSends in Gen.Int[0, 12].Array[0, 3]
        from sourceBreaks in Gen.Select(Gen.Int[0, 4], Gen.Int[0, 12_000]).Array[0, 2]
        select new Scenario(length, chunkSize, hasVersion, verifiable, withChecksum, maxRetries, failedSends, sourceBreaks);

    private static readonly string[] InjectedErrors = ["The connection was reset", "The source broke off"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public Task Every_upload_commits_exactly_the_content_or_nothing()
    {
        return ScenarioGen.SampleAsync(RunAsync, iter: 300, threads: 1, writeLine: TestContext.Current.TestOutputHelper!.WriteLine);
    }

    private static async Task RunAsync(Scenario scenario)
    {
        var content = new byte[scenario.Length];
        new Random(scenario.Length).NextBytes(content);
        var faulty = new StrongBox<bool>(true);
        var server = new ChunkedUploadServer
        {
            OnSend = send => faulty.Value && scenario.FailedSends.Contains(send) ? throw new IOException("The connection was reset") : Task.CompletedTask
        };
        var target = server.Target(scenario.ChunkSize, scenario.Verifiable);
        var operation = new TransferClient(Options(scenario, content)).From(Source(content, scenario, faulty)).To(target).Build();

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

        var uploads = target.Sessions.Select(session => server.Get(session.ResumeToken!)).Distinct().ToList();
        if (scenario.FailedSends.Length == 0 && scenario.SourceBreaks.Length == 0)
        {
            Assert.NotNull(result);
        }
        if (result != null)
        {
            Assert.Equal(content, Assert.Single(uploads, upload => upload.Completed).Data);
            Assert.Equal(content.Length, result.Transferred);
        }
        else
        {
            // Never committed, and only because of an injected fault: a chunk at the wrong offset fails differently
            Assert.DoesNotContain(uploads, upload => upload.Completed);
            Assert.Contains(failure!.Error.Message, InjectedErrors);
        }

        if (failure != null && operation.GetCheckpoint() is { } checkpoint)
        {
            var upload = server.Get(checkpoint.ResumeToken!);
            var part = Assert.Single(checkpoint.Parts);
            Assert.True(part.Completed <= upload.Acknowledged, $"The checkpoint counts {part.Completed} bytes, the server acknowledged {upload.Acknowledged}");

            faulty.Value = false;
            var resumed = await new TransferClient(Options(scenario, content)).From(Source(content, scenario, faulty)).To(target)
                .ResumeFrom(checkpoint).RunAsync(cancellationToken: Token);
            Assert.Equal(content.Length, resumed.Transferred);
            Assert.Equal(content, target.Sessions.Select(session => server.Get(session.ResumeToken!)).Last(candidate => candidate.Completed).Data);
        }
    }

    private static MemorySource Source(byte[] content, Scenario scenario, StrongBox<bool> faulty) => new(content)
    {
        Version = scenario.HasVersion ? "v1" : null,
        WrapContent = (read, _, stream) => faulty.Value && scenario.SourceBreaks.Any(fault => fault.Read == read)
            ? new FailingStream(stream, scenario.SourceBreaks.First(fault => fault.Read == read).Offset, () => new IOException("The source broke off"))
            : stream
    };

    private static TransferOptions Options(Scenario scenario, byte[] content) => new()
    {
        BufferSize = 700,
        Retry = new RetryPolicy { MaxRetries = scenario.MaxRetries, InitialDelay = TimeSpan.Zero, Jitter = false },
        ExpectedChecksum = scenario.WithChecksum ? new Checksum(HashAlgorithmName.SHA256, SHA256.HashData(content)) : null
    };
}
