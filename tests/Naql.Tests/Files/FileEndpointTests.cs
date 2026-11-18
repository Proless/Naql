using Naql.Files;
using Naql.Tests.Fakes;

namespace Naql.Tests.Files;

/// <summary>
/// File sources and targets.
/// </summary>
public sealed class FileEndpointTests : IDisposable
{
    private static readonly TransferOptions Options = new()
    {
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false },
        MinPartSize = 1_000
    };

    private static readonly TransferOptions Parallel = Options with { MaxConnections = 4 };

    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        new Random(7).NextBytes(content);
        return content;
    }

    private static Task<TransferResult> RunAsync(ITransferSource source, ITransferTarget target, TransferOptions? options = null)
    {
        return new TransferClient(options ?? Options).RunAsync(source, target, cancellationToken: Token);
    }

    private string CreateFile(string name, byte[] content)
    {
        var path = _temp.File(name);
        File.WriteAllBytes(path, content);
        return path;
    }

    #region FileSource

    [Fact]
    public async Task Copies_a_file_in_parallel_parts()
    {
        var content = Content(50_000);
        var source = CreateFile("in.bin", content);
        var target = _temp.File("out.bin");

        var result = await RunAsync(FileSource.At(source), FileTarget.At(target), Parallel);

        Assert.Equal(content, File.ReadAllBytes(target));
        Assert.Equal(4, result.Parts);
    }

    [Fact]
    public async Task Describes_the_file()
    {
        var path = CreateFile("in.bin", Content(1_234));

        var info = await FileSource.At(path).GetInfoAsync(Token);

        Assert.Equal(1_234, info.Length);
        Assert.Equal("in.bin", info.Name);
        Assert.True(info.SupportsRanges);
        Assert.NotNull(info.Version);
        Assert.Equal(Path.GetFullPath(path), info.Identity);
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(path)), info.LastModified);
    }

    [Fact]
    public async Task Reads_only_the_requested_range()
    {
        var content = Content(1_000);
        var source = FileSource.At(CreateFile("in.bin", content));

        using var read = await source.OpenReadAsync(new ReadRequest(new ByteRange(100, 50)), Token);
        using var copy = new MemoryStream();
        await read.Content.CopyToAsync(copy, Token);

        Assert.Equal(content.AsSpan(100, 50).ToArray(), copy.ToArray());
    }

    [Fact]
    public async Task Notices_when_the_file_changed()
    {
        var source = FileSource.At(CreateFile("in.bin", Content(100)));

        var ex = await Assert.ThrowsAsync<TransferException>(() => source.OpenReadAsync(new ReadRequest(new ByteRange(10), "0:0"), Token));

        Assert.Equal(TransferErrorKind.SourceChanged, ex.Kind);
    }

    [Fact]
    public async Task A_missing_file_fails_without_retrying()
    {
        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(FileSource.At(_temp.File("missing.bin")), new MemoryTarget()));

        Assert.Equal(TransferErrorKind.NotFound, ex.Kind);
        Assert.Equal(TransferSide.Source, ex.Side);
    }

    #endregion

    #region FileTarget

    [Fact]
    public async Task Writes_through_a_partial_file_and_renames_it_at_the_end()
    {
        var content = Content(10_000);
        var path = _temp.File("sub/dir/out.bin");
        var sawPartial = false;
        var source = new MemorySource(content)
        {
            OnOpen = (_, _, _) =>
            {
                sawPartial = File.Exists(path + FileTarget.PartialExtension) || sawPartial;
                return Task.CompletedTask;
            }
        };
        var target = FileTarget.At(path);

        var result = await RunAsync(source, target, Parallel);

        Assert.True(sawPartial);
        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + FileTarget.PartialExtension));
        Assert.Equal(Path.GetFullPath(path), result.Location);
    }

    [Theory]
    [InlineData("../escape.bin", "escape.bin")]
    [InlineData("a:b?.bin", "a_b_.bin")]
    [InlineData("..", "download")]
    [InlineData(null, "download")]
    public async Task Names_files_safely_inside_the_directory(string? name, string expected)
    {
        var directory = _temp.File("out");
        var source = new MemorySource([1, 2, 3]);
        var named = new NamedSource(source, name);

        await RunAsync(named, FileTarget.InDirectory(directory));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(directory, expected)));
    }

    [Fact]
    public async Task A_name_selector_overrides_the_source_name()
    {
        var target = FileTarget.InDirectory(_temp.Path, info => $"{info.Length}.bin");

        var result = await RunAsync(new MemorySource(Content(42)), target);

        Assert.Equal(_temp.File("42.bin"), result.Location);
    }

    [Fact]
    public async Task Overwrites_an_existing_file_by_default()
    {
        var path = CreateFile("out.bin", Content(5_000));
        var content = Content(100);

        await RunAsync(new MemorySource(content), FileTarget.At(path));

        Assert.Equal(content, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Fails_on_an_existing_file_when_asked_to()
    {
        var existing = Content(5_000);
        var path = CreateFile("out.bin", existing);

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(new MemorySource(Content(100)), FileTarget.At(path, FileConflict.Fail)));

        Assert.Equal(TransferErrorKind.AlreadyExists, ex.Kind);
        Assert.Equal(existing, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Keeps_both_files_when_asked_to()
    {
        var existing = Content(5_000);
        CreateFile("memory.bin", existing);
        var content = Content(100);
        var target = FileTarget.InDirectory(_temp.Path, conflict: FileConflict.KeepBoth);

        var result = await RunAsync(new MemorySource(content), target);

        Assert.Equal(_temp.File("memory (1).bin"), result.Location);
        Assert.Equal(content, File.ReadAllBytes(result.Location!));
        Assert.Equal(existing, File.ReadAllBytes(_temp.File("memory.bin")));
    }

    [Fact]
    public async Task Discarding_after_a_failure_deletes_the_partial_file()
    {
        var path = _temp.File("out.bin");
        var source = new MemorySource(Content(10_000)) { WrapContent = (_, _, stream) => new FailingStream(stream, 1_000, () => new IOException("Reset")) };
        var options = Options with { Retry = RetryPolicy.None, DiscardPartialOnFailure = true };

        await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, FileTarget.At(path), options));

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + FileTarget.PartialExtension));
    }

    [Fact]
    public async Task A_resume_token_continues_the_partial_file()
    {
        var content = Content(10_000);
        var info = new SourceInfo { Length = content.Length };
        var path = _temp.File("out.bin");

        string token;
        await using (var first = await FileTarget.At(path).OpenAsync(new TargetOpenRequest(info), Token))
        {
            await first.WritePartAsync(new ByteRange(0, 4_000), new MemoryStream(content, 0, 4_000), Token);
            token = first.ResumeToken!;
        }

        await using (var second = await FileTarget.At(path).OpenAsync(new TargetOpenRequest(info, token), Token))
        {
            await second.WritePartAsync(new ByteRange(4_000, 6_000), new MemoryStream(content, 4_000, 6_000), Token);
            await second.CompleteAsync(Token);
        }

        Assert.Equal(content, File.ReadAllBytes(path));
    }

    #endregion

    /// <summary>
    /// Gives another source's content a different name.
    /// </summary>
    private sealed class NamedSource(ITransferSource inner, string? name) : ITransferSource
    {
        public async Task<SourceInfo> GetInfoAsync(CancellationToken cancellationToken) => (await inner.GetInfoAsync(cancellationToken)) with { Name = name };

        public async Task<SourceStream> OpenReadAsync(ReadRequest request, CancellationToken cancellationToken)
        {
            var stream = await inner.OpenReadAsync(request, cancellationToken);
            return new SourceStream(stream.Content, stream.Info with { Name = name });
        }
    }
}
