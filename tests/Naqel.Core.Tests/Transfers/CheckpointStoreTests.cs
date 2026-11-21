using Naqel.Files;
using Naqel.Core.Tests.Fakes;

namespace Naqel.Core.Tests.Transfers;

/// <summary>
/// Transfers that keep their own checkpoint in a store and continue by themselves.
/// </summary>
public sealed class CheckpointStoreTests : IDisposable
{
    private static readonly TransferOptions Options = new()
    {
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false },
        MinSplitPartSize = 1_000,
        BufferSize = 1_000,
        CheckpointInterval = TimeSpan.FromMilliseconds(20)
    };

    private readonly TempDirectory _temp = new();
    private readonly byte[] _content = Content(10_000);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        new Random(13).NextBytes(content);
        return content;
    }

    /// <summary>
    /// Runs a transfer with <paramref name="store"/> until its first read stalls after 3,000 bytes, then cancels it.
    /// </summary>
    private async Task CancelWhenStalledAsync(ITransferTarget target, ICheckpointStore store, Func<bool>? ready = null)
    {
        StallingStream? stalling = null;
        var source = new MemorySource(_content)
        {
            WrapContent = (index, _, stream) => index == 0 ? stalling = new StallingStream(stream, 3_000, honorCancellation: true) : stream
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var run = new TransferClient(Options).From(source).To(target).WithCheckpointStore(store).RunAsync(cancellationToken: cts.Token);
        while (stalling is not { Stalled.IsCompleted: true } || !(ready?.Invoke() ?? true))
        {
            Assert.False(run.IsCompleted, "The transfer ended before it stalled");
            await Task.Delay(10, Token);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    #region FileCheckpointStore

    [Fact]
    public async Task A_file_store_keeps_a_checkpoint_between_instances()
    {
        var path = _temp.File("sub/out.bin.naqel-checkpoint");
        var checkpoint = new TransferCheckpoint(100, null, "\"v1\"", "token", [new CheckpointPart(new ByteRange(0, 100), 40)]);

        await new FileCheckpointStore(path).SaveAsync(checkpoint, Token);
        var loaded = await new FileCheckpointStore(path).LoadAsync(Token);

        Assert.Equal(40, loaded?.Completed);
        Assert.Equal("\"v1\"", loaded?.SourceVersion);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task A_file_store_without_a_readable_checkpoint_has_none()
    {
        var store = FileCheckpointStore.For(_temp.File("out.bin"));
        Assert.Equal(_temp.File("out.bin") + FileCheckpointStore.Extension, store.Path);
        Assert.Null(await store.LoadAsync(Token));

        await File.WriteAllTextAsync(store.Path, "not a checkpoint", Token);
        Assert.Null(await store.LoadAsync(Token));

        await store.DeleteAsync(Token);
        Assert.False(File.Exists(store.Path));
        await store.DeleteAsync(Token);

        // Nothing to delete in a directory that was never created
        await new FileCheckpointStore(_temp.File("missing/out.naqel-checkpoint")).DeleteAsync(Token);
    }

    #endregion

    #region Automatic checkpoints

    [Fact]
    public async Task A_canceled_transfer_saves_its_checkpoint_and_the_next_run_continues_it()
    {
        var path = _temp.File("out.bin");
        var store = FileCheckpointStore.For(path);

        await CancelWhenStalledAsync(FileTarget.At(path), store);

        Assert.Equal(3_000, (await store.LoadAsync(Token))?.Completed);

        // The same transfer again, as after a restart: no checkpoint handling in the caller's code
        var source = new MemorySource(_content);
        var result = await new TransferClient(Options).From(source).To(FileTarget.At(path)).WithCheckpointStore(store).RunAsync(cancellationToken: Token);

        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.Equal(3_000, result.Resumed);
        Assert.Equal(new ByteRange(3_000, 7_000), Assert.Single(source.Reads).Range);
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public async Task The_checkpoint_is_saved_while_the_transfer_runs()
    {
        var store = new RecordingStore();

        // Cancel only once a save during the run has seen the stalled bytes
        await CancelWhenStalledAsync(new MemoryTarget(), store, () => store.Saved?.Completed == 3_000);

        Assert.True(store.Saves >= 1);
        Assert.Equal(3_000, store.Saved?.Completed);
    }

    [Fact]
    public async Task An_unchanged_checkpoint_isnt_saved_again()
    {
        var store = new RecordingStore();
        await CancelWhenStalledAsync(new MemoryTarget(), store, () => store.Saved?.Completed == 3_000);
        var saves = store.Saves;

        await Task.Delay(100, Token);

        // Nothing changed after the first save of 3,000 bytes, and the final save found the same checkpoint
        Assert.Equal(saves, store.Saves);
    }

    [Fact]
    public async Task A_completed_transfer_deletes_its_checkpoint()
    {
        var store = new RecordingStore();

        await new TransferClient(Options).From(new MemorySource(_content)).To(new MemoryTarget()).WithCheckpointStore(store).RunAsync(cancellationToken: Token);

        Assert.Null(store.Saved);
        Assert.Equal(1, store.Deletes);
    }

    [Fact]
    public async Task A_transfer_that_cant_be_continued_deletes_its_checkpoint()
    {
        var store = new RecordingStore();
        var target = new MemoryTarget { Capabilities = new() { ResumableParts = true } };

        await CancelWhenStalledAsync(target, store);

        Assert.Null(store.Saved);
        Assert.Equal(1, store.Deletes);
    }

    [Fact]
    public async Task An_explicit_checkpoint_wins_over_the_stored_one()
    {
        var target = new MemoryTarget();
        var store = new RecordingStore();
        await CancelWhenStalledAsync(target, store);
        var stale = new TransferCheckpoint(_content.Length, null, "v0", "memory-0", [new CheckpointPart(new ByteRange(0, _content.Length), 0)]);
        var source = new MemorySource(_content);

        // The stored checkpoint says 3,000 bytes; the explicit one has the wrong version, so the transfer starts over
        var result = await new TransferClient(Options).From(source).To(target).ResumeFrom(stale).WithCheckpointStore(store).RunAsync(cancellationToken: Token);

        Assert.Equal(0, result.Resumed);
        Assert.Equal(ByteRange.All, Assert.Single(source.Reads).Range);
    }

    [Fact]
    public async Task A_store_that_cant_load_means_starting_over()
    {
        var store = new RecordingStore { FailLoading = true };
        var target = new MemoryTarget();

        var result = await new TransferClient(Options).From(new MemorySource(_content)).To(target).WithCheckpointStore(store).RunAsync(cancellationToken: Token);

        Assert.Equal(_content, target.Session.Data);
        Assert.Equal(0, result.Resumed);
    }

    [Fact]
    public async Task A_store_that_doesnt_answer_doesnt_hang_the_transfer()
    {
        var store = new RecordingStore { Hang = true };
        var target = new MemoryTarget();
        var options = Options with { ResponseTimeout = TimeSpan.FromMilliseconds(200) };

        var run = new TransferClient(options).From(new MemorySource(_content)).To(target).WithCheckpointStore(store).RunAsync(cancellationToken: Token);

        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(_content, target.Session.Data);
    }

    [Fact]
    public async Task A_failing_store_doesnt_fail_the_transfer()
    {
        var store = new RecordingStore { Fail = true };
        var target = new MemoryTarget();

        await new TransferClient(Options).From(new MemorySource(_content)).To(target).WithCheckpointStore(store).RunAsync(cancellationToken: Token);

        Assert.Equal(_content, target.Session.Data);
    }

    #endregion

    /// <summary>
    /// Keeps the checkpoint in memory and counts what the engine did with it.
    /// </summary>
    private sealed class RecordingStore : ICheckpointStore
    {
        private TransferCheckpoint? _saved;
        private int _saves;
        private int _deletes;

        public bool Fail { get; init; }
        public TransferCheckpoint? Saved => Volatile.Read(ref _saved);
        public int Saves => Volatile.Read(ref _saves);
        public int Deletes => Volatile.Read(ref _deletes);

        public bool FailLoading { get; init; }
        public bool Hang { get; init; }

        public async Task<TransferCheckpoint?> LoadAsync(CancellationToken cancellationToken)
        {
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, CancellationToken.None);
            }
            if (FailLoading)
            {
                throw new UnauthorizedAccessException("The store can't be read");
            }
            return Saved;
        }

        public Task SaveAsync(TransferCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new IOException("The store is broken");
            }
            Interlocked.Increment(ref _saves);
            Volatile.Write(ref _saved, checkpoint);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new IOException("The store is broken");
            }
            Interlocked.Increment(ref _deletes);
            Volatile.Write(ref _saved, null);
            return Task.CompletedTask;
        }
    }
}
