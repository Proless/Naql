using Naql.Files;

namespace Naql.Tests.Files;

/// <summary>
/// Resolving <c>file:</c> references into sources and <c>file:</c> destinations into targets.
/// </summary>
public sealed class FileResolverTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private string Create(string relative, int length)
    {
        var path = _temp.File(relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = new byte[length];
        new Random(length).NextBytes(content);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static Uri Folder(string path) => new(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);

    private static async Task<List<ResolvedSource>> SourcesAsync(Uri reference, FileSourceResolver? resolver = null)
    {
        var all = new List<ResolvedSource>();
        await foreach (var item in Resolve.SourcesAsync(reference, [resolver ?? new FileSourceResolver()], cancellationToken: Token))
        {
            all.Add(item);
        }
        return all;
    }

    [Fact]
    public async Task A_file_resolves_to_its_source()
    {
        var path = Create("in/report.pdf", 1_234);

        var item = Assert.Single(await SourcesAsync(new Uri(path)));

        Assert.Equal("", item.Path);
        Assert.Equal(1_234, item.Info!.Length);
        Assert.Equal("report.pdf", item.Info.Name);
        Assert.Equal(1_234, (await item.Source.GetInfoAsync(Token)).Length);
    }

    [Fact]
    public async Task A_folder_resolves_to_its_files_with_their_paths_but_not_unfinished_transfers()
    {
        Create("in/a.txt", 1);
        Create("in/sub/b.txt", 2);
        Create("in/sub/deeper/c.txt", 3);
        Create("in/sub/d.txt" + FileTarget.PartialExtension, 4);
        Create("in/e.txt" + FileCheckpointStore.Extension, 5);

        var all = await SourcesAsync(Folder(_temp.File("in")));

        Assert.Equal(["a.txt", "sub/b.txt", "sub/deeper/c.txt"], all.Select(item => item.Path).OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_folder_can_leave_out_its_subfolders()
    {
        Create("in/a.txt", 1);
        Create("in/sub/b.txt", 2);

        var all = await SourcesAsync(new Uri(_temp.File("in")), new FileSourceResolver(recursive: false));

        Assert.Equal("a.txt", Assert.Single(all).Path);
    }

    [Fact]
    public async Task A_missing_file_isnt_found()
    {
        var ex = await Assert.ThrowsAsync<TransferException>(() => SourcesAsync(new Uri(_temp.File("missing.bin"))));

        Assert.Equal(TransferErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public void Only_file_references_are_resolved()
    {
        Assert.False(new FileSourceResolver().CanResolve(new Uri("https://example.com/a.bin")));
        Assert.False(new FileTargetResolver().CanResolve(new Uri("dropbox:/a")));
    }

    [Fact]
    public async Task A_relative_path_goes_inside_the_destination_folder_with_safe_names()
    {
        var destination = Folder(_temp.File("out"));
        var source = new MemoryContent([1, 2, 3]);

        var target = await new FileTargetResolver().ResolveAsync(destination, "CON/a:b?.txt", null, Token);
        var result = await Transfer.From(source).To(target).RunAsync(cancellationToken: Token);

        Assert.Equal(Path.Combine(_temp.File("out"), "_CON", "a_b_.txt"), result.Location);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(result.Location!));
    }

    [Fact]
    public async Task A_single_item_in_a_destination_folder_is_named_from_its_source()
    {
        var destination = Folder(_temp.File("out"));

        var target = await new FileTargetResolver().ResolveAsync(destination, "", null, Token);
        var result = await Transfer.From(new MemoryContent([7])).To(target).RunAsync(cancellationToken: Token);

        Assert.Equal(Path.Combine(_temp.File("out"), "memory.bin"), result.Location);
    }

    [Fact]
    public async Task A_destination_that_isnt_a_folder_is_the_file_itself()
    {
        var destination = new Uri(_temp.File("exact.bin"));

        var target = await new FileTargetResolver().ResolveAsync(destination, "", null, Token);
        var result = await Transfer.From(new MemoryContent([7])).To(target).RunAsync(cancellationToken: Token);

        Assert.Equal(_temp.File("exact.bin"), result.Location);
    }

    [Fact]
    public async Task A_file_cant_hold_a_relative_path()
    {
        var file = Create("existing.bin", 1);

        var ex = await Assert.ThrowsAsync<TransferException>(async () => await new FileTargetResolver().ResolveAsync(new Uri(file), "a/b.txt", null, Token));

        Assert.Equal(TransferErrorKind.AlreadyExists, ex.Kind);
    }

    [Fact]
    public async Task A_path_that_would_leave_the_destination_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await new FileTargetResolver().ResolveAsync(Folder(_temp.File("out")), "../escape.txt", null, Token));
    }

    [Fact]
    public async Task A_folder_tree_copies_through_resolvers_alone()
    {
        Create("in/a.txt", 100);
        Create("in/sub/b.txt", 20_000);
        Create("in/sub/deeper/c.txt", 0);
        var destination = Folder(_temp.File("copy"));
        ITargetResolver[] targets = [new FileTargetResolver()];

        await foreach (var item in Resolve.SourcesAsync(Folder(_temp.File("in")), [new FileSourceResolver()], cancellationToken: Token))
        {
            var target = await Resolve.TargetAsync(destination, item.Path, item.Info, targets, Token);
            await Transfer.From(item.Source).To(target).RunAsync(cancellationToken: Token);
        }

        foreach (var relative in new[] { "a.txt", "sub/b.txt", "sub/deeper/c.txt" })
        {
            var native = relative.Replace('/', Path.DirectorySeparatorChar);
            Assert.Equal(File.ReadAllBytes(Path.Combine(_temp.File("in"), native)), File.ReadAllBytes(Path.Combine(_temp.File("copy"), native)));
        }
    }

    /// <summary>
    /// A small in-memory source named memory.bin.
    /// </summary>
    private sealed class MemoryContent(byte[] content) : ITransferSource
    {
        private readonly Fakes.MemorySource _inner = new(content);

        public Task<SourceInfo> GetInfoAsync(CancellationToken cancellationToken) => _inner.GetInfoAsync(cancellationToken);

        public Task<SourceRead> OpenReadAsync(ReadRequest request, CancellationToken cancellationToken) => _inner.OpenReadAsync(request, cancellationToken);
    }
}
