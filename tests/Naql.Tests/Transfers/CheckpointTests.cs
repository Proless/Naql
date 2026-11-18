using Naql.Files;
using Naql.Tests.Fakes;

namespace Naql.Tests.Transfers;

/// <summary>
/// Saving what an unfinished transfer kept, and continuing it in a later run.
/// </summary>
public sealed class CheckpointTests : IDisposable
{
    private static readonly TransferOptions Options = new()
    {
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false },
        MinSplitPartSize = 1_000,
        BufferSize = 1_000
    };

    private readonly TempDirectory _temp = new();
    private readonly byte[] _content = Content(10_000);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        new Random(11).NextBytes(content);
        return content;
    }

    /// <summary>
    /// A source whose reads matching <paramref name="stallWhen"/> stall after <paramref name="stallAfter"/> bytes.
    /// </summary>
    private MemorySource StallingSource(List<StallingStream> stalls, long stallAfter = 3_000, Func<ReadRequest, bool>? stallWhen = null, string? version = "v1",
        bool supportsRanges = true)
    {
        return new MemorySource(_content)
        {
            Version = version,
            SupportsRanges = supportsRanges,
            WrapContent = (_, request, stream) =>
            {
                if (!(stallWhen?.Invoke(request) ?? true))
                {
                    return stream;
                }
                var stalling = new StallingStream(stream, stallAfter, honorCancellation: true);
                lock (stalls)
                {
                    stalls.Add(stalling);
                }
                return stalling;
            }
        };
    }

    /// <summary>
    /// Runs a transfer until <paramref name="ready"/> holds and a read stalls, then cancels it.
    /// </summary>
    private static async Task<(TransferOperation Operation, TransferCheckpoint? Live)> CancelWhenStalledAsync(
        ITransferSource source, ITransferTarget target, List<StallingStream> stalls, TransferOptions? options = null, Func<bool>? ready = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var operation = new TransferClient(options ?? Options).From(source).To(target).Build();
        var run = operation.RunAsync(cancellationToken: cts.Token);

        while (true)
        {
            Task? stalled;
            lock (stalls)
            {
                stalled = stalls.FirstOrDefault()?.Stalled;
            }
            if (stalled is { IsCompleted: true } && (ready?.Invoke() ?? true))
            {
                break;
            }
            Assert.False(run.IsCompleted, "The transfer ended before it stalled");
            await Task.Delay(10, Token);
        }

        var live = operation.GetCheckpoint();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        return (operation, live);
    }

    private static Task<TransferResult> ResumeAsync(ITransferSource source, ITransferTarget target, TransferCheckpoint checkpoint, TransferOptions? options = null)
    {
        return new TransferClient(options ?? Options).From(source).To(target).ResumeFrom(checkpoint).RunAsync(cancellationToken: Token);
    }

    #region Format

    [Fact]
    public void A_checkpoint_survives_serialization()
    {
        var checkpoint = new TransferCheckpoint(10_000, "ABC123", "\"v1\" with spaces,\nnewlines & %25", @"C:\dir\file.bin.naql-part",
            [new CheckpointPart(new ByteRange(0, 5_000), 5_000), new CheckpointPart(new ByteRange(5_000, 5_000), 1_234)]);

        var copy = TransferCheckpoint.Deserialize(checkpoint.Serialize());

        Assert.Equal(checkpoint.Length, copy.Length);
        Assert.Equal(checkpoint.SourceIdentity, copy.SourceIdentity);
        Assert.Equal(checkpoint.SourceVersion, copy.SourceVersion);
        Assert.Equal(checkpoint.ResumeToken, copy.ResumeToken);
        Assert.Equal(checkpoint.Parts.Select(part => (part.Range, part.Completed)), copy.Parts.Select(part => (part.Range, part.Completed)));
        Assert.Equal(6_234, copy.Completed);
        Assert.True(copy.Parts[0].IsComplete);
        Assert.False(copy.Parts[1].IsComplete);
    }

    [Fact]
    public void Missing_values_survive_serialization()
    {
        var copy = TransferCheckpoint.Deserialize(new TransferCheckpoint(null, null, null, null, [new CheckpointPart(ByteRange.All, 42)]).Serialize());

        Assert.Null(copy.Length);
        Assert.Null(copy.SourceIdentity);
        Assert.Null(copy.SourceVersion);
        Assert.Null(copy.ResumeToken);
        Assert.Equal(ByteRange.All, Assert.Single(copy.Parts).Range);
        Assert.Equal(42, copy.Parts[0].Completed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    // Version 1 had no identity, so it's not read
    [InlineData("naql-checkpoint 1\nlength 10\nversion -\ntoken -\npart 0 10 0\n")]
    [InlineData("naql-checkpoint 3\nlength 10\nidentity -\nversion -\ntoken -\npart 0 10 0\n")]
    [InlineData("naql-checkpoint 2\nlength 10\nidentity -\nversion -\n")]
    [InlineData("naql-checkpoint 2\nlength ten\nidentity -\nversion -\ntoken -\npart 0 10 0\n")]
    [InlineData("naql-checkpoint 2\nlength 10\nidentity -\nversion v1\ntoken -\npart 0 10 0\n")]
    [InlineData("naql-checkpoint 2\nlength 10\nidentity -\nversion -\ntoken -\npart 0 10 11\n")]
    [InlineData("naql-checkpoint 2\nlength 10\nidentity -\nversion -\ntoken -\npart 0 10\n")]
    // No parts, a gap, an overlap, parts out of order, short of the end, past the end, open-ended with a known length
    [InlineData("naql-checkpoint 2\nlength 10\nidentity -\nversion -\ntoken -\n")]
    [InlineData("naql-checkpoint 2\nlength 10\nidentity -\nversion -\ntoken -\npart 0 4 0\npart 5 5 0\n")]
    [InlineData("naql-checkpoint 2\nlength 10\nidentity -\nversion -\ntoken -\npart 0 6 0\npart 5 5 0\n")]
    [InlineData("naql-checkpoint 2\nlength 10\nidentity -\nversion -\ntoken -\npart 5 5 0\npart 0 5 0\n")]
    [InlineData("naql-checkpoint 2\nlength 10\nidentity -\nversion -\ntoken -\npart 0 5 0\n")]
    [InlineData("naql-checkpoint 2\nlength 10\nidentity -\nversion -\ntoken -\npart 0 5 0\npart 5 6 0\n")]
    [InlineData("naql-checkpoint 2\nlength 10\nidentity -\nversion -\ntoken -\npart 0 - 0\n")]
    [InlineData("naql-checkpoint 2\nlength -\nidentity -\nversion -\ntoken -\npart 0 - 0\npart 0 5 0\n")]
    public void Rejects_text_that_isnt_a_checkpoint(string text)
    {
        Assert.Throws<FormatException>(() => TransferCheckpoint.Deserialize(text));
    }

    [Fact]
    public void Parts_must_cover_the_content_exactly_once()
    {
        Assert.Throws<ArgumentException>(() => new TransferCheckpoint(10, null, "v1", "t", [new CheckpointPart(new ByteRange(0, 4), 0), new CheckpointPart(new ByteRange(5, 5), 0)]));
        Assert.Throws<ArgumentException>(() => new TransferCheckpoint(10, null, "v1", "t", []));

        var checkpoint = new TransferCheckpoint(10, null, "v1", "t", [new CheckpointPart(new ByteRange(0, 5), 5), new CheckpointPart(new ByteRange(5, 5), 0)]);
        Assert.Equal(5, checkpoint.Completed);
    }

    [Fact]
    public void A_checkpoint_keeps_a_fingerprint_of_the_identity_instead_of_the_identity()
    {
        const string url = "https://user:secret@example.com/file.zip?signature=abc";

        var fingerprint = TransferCheckpoint.Fingerprint(url);

        Assert.Equal(64, fingerprint!.Length);
        Assert.DoesNotContain("secret", fingerprint);
        Assert.Equal(fingerprint, TransferCheckpoint.Fingerprint(url));
        Assert.NotEqual(fingerprint, TransferCheckpoint.Fingerprint("https://example.com/other.zip"));
        Assert.Null(TransferCheckpoint.Fingerprint(null));
    }

    #endregion

    #region Taking checkpoints

    [Fact]
    public async Task There_is_no_checkpoint_before_running_or_after_completing()
    {
        var operation = Transfer.From(new MemorySource(_content)).To(new MemoryTarget()).Build();
        Assert.Null(operation.GetCheckpoint());

        await operation.RunAsync(cancellationToken: Token);

        Assert.Null(operation.GetCheckpoint());
    }

    [Fact]
    public async Task A_canceled_transfer_reports_what_the_target_kept()
    {
        var stalls = new List<StallingStream>();
        var target = new MemoryTarget();

        var (operation, live) = await CancelWhenStalledAsync(StallingSource(stalls), target, stalls);
        var checkpoint = operation.GetCheckpoint();

        Assert.NotNull(checkpoint);
        Assert.Equal(10_000, checkpoint.Length);
        Assert.Equal("v1", checkpoint.SourceVersion);
        Assert.Equal("memory-0", checkpoint.ResumeToken);
        var part = Assert.Single(checkpoint.Parts);
        Assert.Equal(new ByteRange(0, 10_000), part.Range);
        Assert.Equal(3_000, part.Completed);
        // Taken while the transfer ran, the checkpoint already knew the same
        Assert.Equal(3_000, live?.Completed);
    }

    [Fact]
    public async Task A_target_that_cant_keep_partial_parts_only_reports_finished_parts()
    {
        var stalls = new List<StallingStream>();
        var target = new MemoryTarget { Capabilities = new() { ParallelParts = true, Resumable = true } };

        var (operation, _) = await CancelWhenStalledAsync(StallingSource(stalls), target, stalls);

        Assert.Equal(0, operation.GetCheckpoint()!.Completed);
    }

    public static TheoryData<string> NoCheckpointCases => ["target can't resume", "partial data discarded", "source has no version", "source has no ranges"];

    [Theory]
    [MemberData(nameof(NoCheckpointCases))]
    public async Task There_is_no_checkpoint_when_the_transfer_cant_be_continued(string reason)
    {
        var stalls = new List<StallingStream>();
        var target = reason == "target can't resume" ? new MemoryTarget { Capabilities = new() { ResumableParts = true } } : new MemoryTarget();
        var source = StallingSource(stalls, version: reason == "source has no version" ? null : "v1", supportsRanges: reason != "source has no ranges");
        var options = Options with { DiscardPartialOnFailure = reason == "partial data discarded" };

        var (operation, _) = await CancelWhenStalledAsync(source, target, stalls, options);

        Assert.Null(operation.GetCheckpoint());
    }

    #endregion

    #region Resuming

    [Fact]
    public async Task Resuming_continues_where_the_target_stopped()
    {
        var stalls = new List<StallingStream>();
        var target = new MemoryTarget();
        var (operation, _) = await CancelWhenStalledAsync(StallingSource(stalls), target, stalls);
        var source = new MemorySource(_content);

        var result = await ResumeAsync(source, target, operation.GetCheckpoint()!);

        Assert.Equal(_content, target.Sessions[1].Data);
        Assert.Equal("memory-0", target.Sessions[1].Request.ResumeToken);
        Assert.True(target.Sessions[1].Completed);
        Assert.Equal(new ByteRange(3_000, 7_000), Assert.Single(source.Reads).Range);
        Assert.Equal(3_000, result.Resumed);
        Assert.Equal(10_000, result.Transferred);
    }

    [Fact]
    public async Task Resuming_a_parallel_transfer_only_moves_the_unfinished_parts()
    {
        var stalls = new List<StallingStream>();
        var target = new MemoryTarget();
        var options = Options with { MaxConnections = 4 };
        var source = StallingSource(stalls, stallAfter: 1_000, stallWhen: request => request.Range.Start == 5_000);

        var (operation, _) = await CancelWhenStalledAsync(source, target, stalls, options, () => target.Session.CompletedWrites == 3);
        var checkpoint = operation.GetCheckpoint()!;
        Assert.Equal(new[] { 2_500L, 2_500, 1_000, 2_500 }, checkpoint.Parts.Select(part => part.Completed));

        var second = new MemorySource(_content);
        var result = await ResumeAsync(second, target, checkpoint, options);

        Assert.Equal(new ByteRange(6_000, 1_500), Assert.Single(second.Reads).Range);
        Assert.Equal(_content, target.Sessions[1].Data);
        Assert.Equal(8_500, result.Resumed);
        Assert.Equal(4, result.Parts);
    }

    [Fact]
    public async Task Resuming_starts_over_when_the_content_changed()
    {
        var stalls = new List<StallingStream>();
        var target = new MemoryTarget();
        var (operation, _) = await CancelWhenStalledAsync(StallingSource(stalls), target, stalls);
        var changed = new MemorySource(_content) { Version = "v2" };

        var result = await ResumeAsync(changed, target, operation.GetCheckpoint()!);

        Assert.Equal(ByteRange.All, Assert.Single(changed.Reads).Range);
        Assert.Null(target.Sessions[1].Request.ResumeToken);
        Assert.Equal(_content, target.Sessions[1].Data);
        Assert.Equal(0, result.Resumed);
    }

    [Fact]
    public async Task A_saved_checkpoint_continues_a_file_download()
    {
        var stalls = new List<StallingStream>();
        var path = _temp.File("out.bin");
        var (operation, _) = await CancelWhenStalledAsync(StallingSource(stalls), FileTarget.At(path), stalls);
        var saved = operation.GetCheckpoint()!.Serialize();
        Assert.False(File.Exists(path));

        // Later, in a new process
        var result = await ResumeAsync(new MemorySource(_content), FileTarget.At(path), TransferCheckpoint.Deserialize(saved));

        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.Equal(3_000, result.Resumed);
        Assert.False(File.Exists(path + FileTarget.PartialExtension));
    }

    [Fact]
    public async Task Resuming_starts_over_when_the_partial_file_is_gone()
    {
        var stalls = new List<StallingStream>();
        var path = _temp.File("out.bin");
        var (operation, _) = await CancelWhenStalledAsync(StallingSource(stalls), FileTarget.At(path), stalls);
        File.Delete(path + FileTarget.PartialExtension);
        var source = new MemorySource(_content);

        var result = await ResumeAsync(source, FileTarget.At(path), operation.GetCheckpoint()!);

        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.Equal(0, result.Resumed);
        Assert.Equal(ByteRange.All, Assert.Single(source.Reads).Range);
    }

    [Fact]
    public async Task Resuming_starts_over_when_the_source_is_other_content_with_the_same_version()
    {
        var stalls = new List<StallingStream>();
        var target = new MemoryTarget();
        var (operation, _) = await CancelWhenStalledAsync(StallingSource(stalls), target, stalls);
        var other = new MemorySource(_content) { Identity = "memory://other" };

        var result = await ResumeAsync(other, target, operation.GetCheckpoint()!);

        Assert.Equal(ByteRange.All, Assert.Single(other.Reads).Range);
        Assert.Null(target.Sessions[1].Request.ResumeToken);
        Assert.Equal(0, result.Resumed);
    }

    [Fact]
    public async Task A_checkpoint_doesnt_hold_the_sources_identity()
    {
        var stalls = new List<StallingStream>();
        var source = StallingSource(stalls);
        var (operation, _) = await CancelWhenStalledAsync(source, new MemoryTarget(), stalls);

        var saved = operation.GetCheckpoint()!;

        Assert.Equal(TransferCheckpoint.Fingerprint(source.Identity), saved.SourceIdentity);
        Assert.DoesNotContain(source.Identity!, saved.Serialize());
    }

    [Fact]
    public async Task A_file_target_doesnt_continue_a_partial_file_of_another_target()
    {
        var stalls = new List<StallingStream>();
        var first = _temp.File("first.bin");
        var (operation, _) = await CancelWhenStalledAsync(StallingSource(stalls), FileTarget.At(first), stalls);
        var second = _temp.File("second.bin");

        var result = await ResumeAsync(new MemorySource(_content), FileTarget.At(second), operation.GetCheckpoint()!);

        Assert.Equal(_content, File.ReadAllBytes(second));
        Assert.Equal(0, result.Resumed);
        // The other target's partial file is left alone
        Assert.True(File.Exists(first + FileTarget.PartialExtension));
        Assert.False(File.Exists(first));
    }

    [Fact]
    public async Task A_file_target_in_a_directory_continues_a_partial_file_in_that_directory()
    {
        var stalls = new List<StallingStream>();
        var directory = _temp.File("downloads");
        var (operation, _) = await CancelWhenStalledAsync(StallingSource(stalls), FileTarget.InDirectory(directory), stalls);

        var result = await ResumeAsync(new MemorySource(_content), FileTarget.InDirectory(directory), operation.GetCheckpoint()!);

        Assert.Equal(_content, File.ReadAllBytes(Path.Combine(directory, "memory.bin")));
        Assert.Equal(3_000, result.Resumed);
    }

    [Fact]
    public async Task A_file_target_in_a_directory_doesnt_continue_a_partial_file_elsewhere()
    {
        var stalls = new List<StallingStream>();
        var (operation, _) = await CancelWhenStalledAsync(StallingSource(stalls), FileTarget.InDirectory(_temp.File("a")), stalls);
        var other = _temp.File("b");

        var result = await ResumeAsync(new MemorySource(_content), FileTarget.InDirectory(other), operation.GetCheckpoint()!);

        Assert.Equal(_content, File.ReadAllBytes(Path.Combine(other, "memory.bin")));
        Assert.Equal(0, result.Resumed);
    }

    #endregion
}
