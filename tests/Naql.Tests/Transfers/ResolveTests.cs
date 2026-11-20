using Naql.Tests.Fakes;

namespace Naql.Tests.Transfers;

/// <summary>
/// Resolving references into sources and destinations into targets, with resolvers the caller chooses.
/// </summary>
public sealed class ResolveTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly MemorySource Song1 = new([1]);
    private static readonly MemorySource Song2 = new([2]);
    private static readonly MemorySource Cover = new([3]);

    /// <summary>
    /// Resolves the references of one scheme with a fixed answer per reference.
    /// </summary>
    private sealed class FakeResolver(string scheme, Func<Uri, IEnumerable<Resolved>> resolve) : ISourceResolver
    {
        public List<Uri> Resolved { get; } = [];

        public bool CanResolve(Uri reference) => reference.Scheme == scheme;

        public async IAsyncEnumerable<Resolved> ResolveAsync(Uri reference, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Resolved.Add(reference);
            foreach (var item in resolve(reference))
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }

    private static async Task<List<ResolvedSource>> AllAsync(Uri reference, params ISourceResolver[] resolvers)
    {
        var all = new List<ResolvedSource>();
        await foreach (var item in Resolve.SourcesAsync(reference, resolvers, cancellationToken: Token))
        {
            all.Add(item);
        }
        return all;
    }

    [Fact]
    public async Task References_are_followed_from_resolver_to_resolver_and_paths_join()
    {
        // A short link leads to an album page, which has two songs and a folder with a cover
        var shortener = new FakeResolver("short", _ => [new ResolvedReference(new Uri("album:1"))]);
        var album = new FakeResolver("album", reference => reference.AbsoluteUri == "album:1"
            ? [new ResolvedSource(Song1, "01.mp3"), new ResolvedSource(Song2, "02.mp3"), new ResolvedReference(new Uri("album:art"), "art")]
            : [new ResolvedSource(Cover, "cover.jpg", new SourceInfo { Length = 1 })]);

        var all = await AllAsync(new Uri("short:x"), shortener, album);

        Assert.Equal(["01.mp3", "02.mp3", "art/cover.jpg"], all.Select(item => item.Path));
        Assert.Equal([Song1, Song2, Cover], all.Select(item => (MemorySource)item.Source));
        Assert.Equal(1, all[2].Info!.Length);
    }

    [Fact]
    public async Task Sources_come_out_as_they_are_found()
    {
        var more = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new SlowFolder(more.Task);

        await using var items = Resolve.SourcesAsync(new Uri("slow:folder"), [resolver], cancellationToken: Token).GetAsyncEnumerator(Token);

        Assert.True(await items.MoveNextAsync());
        Assert.Equal("first.bin", items.Current.Path);
        Assert.False(more.Task.IsCompleted);
        more.SetResult(true);
        Assert.True(await items.MoveNextAsync());
        Assert.Equal("second.bin", items.Current.Path);
    }

    private sealed class SlowFolder(Task more) : ISourceResolver
    {
        public bool CanResolve(Uri reference) => true;

        public async IAsyncEnumerable<Resolved> ResolveAsync(Uri reference, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ResolvedSource(Song1, "first.bin");
            await more.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            yield return new ResolvedSource(Song2, "second.bin");
        }
    }

    [Fact]
    public async Task The_first_resolver_that_handles_a_reference_resolves_it()
    {
        var first = new FakeResolver("pick", _ => [new ResolvedSource(Song1)]);
        var second = new FakeResolver("pick", _ => [new ResolvedSource(Song2)]);

        var all = await AllAsync(new Uri("pick:1"), first, second);

        Assert.Same(Song1, Assert.Single(all).Source);
        Assert.Empty(second.Resolved);
    }

    [Fact]
    public async Task A_reference_no_resolver_handles_is_unsupported()
    {
        var ex = await Assert.ThrowsAsync<TransferException>(() => AllAsync(new Uri("https://user:secret@example.com/file?token=abc"),
            new FakeResolver("other", _ => [])));

        Assert.Equal(TransferErrorKind.Unsupported, ex.Kind);
        Assert.Equal(TransferSide.Source, ex.Side);
        // The message shows where, but not credentials
        Assert.Contains("example.com/file", ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
        Assert.DoesNotContain("token", ex.Message);
    }

    [Fact]
    public async Task References_that_lead_back_to_themselves_fail()
    {
        var loop = new FakeResolver("loop", reference => [new ResolvedReference(new Uri(reference.AbsoluteUri == "loop:a" ? "loop:b" : "loop:a"))]);

        var ex = await Assert.ThrowsAsync<TransferException>(() => AllAsync(new Uri("loop:a"), loop));

        Assert.Equal(TransferErrorKind.Protocol, ex.Kind);
        Assert.Equal(2, loop.Resolved.Count);
    }

    [Fact]
    public async Task References_are_followed_only_so_deep()
    {
        var chain = new FakeResolver("chain", reference => [new ResolvedReference(new Uri($"chain:{int.Parse(reference.AbsolutePath) + 1}"))]);

        var ex = await Assert.ThrowsAsync<TransferException>(() => AllAsync(new Uri("chain:0"), chain));

        Assert.Equal(TransferErrorKind.Protocol, ex.Kind);
        Assert.Equal(Resolve.DefaultMaxDepth + 1, chain.Resolved.Count);
    }

    [Fact]
    public async Task The_same_reference_in_two_branches_isnt_a_loop()
    {
        var shared = new FakeResolver("shared", reference => reference.AbsoluteUri == "shared:root"
            ? [new ResolvedReference(new Uri("shared:leaf"), "a"), new ResolvedReference(new Uri("shared:leaf"), "b")]
            : [new ResolvedSource(Song1, "x.bin")]);

        var all = await AllAsync(new Uri("shared:root"), shared);

        Assert.Equal(["a/x.bin", "b/x.bin"], all.Select(item => item.Path));
    }

    [Fact]
    public async Task A_destination_resolves_with_the_first_target_resolver_that_handles_it()
    {
        var target = new MemoryTarget();
        var resolver = new FakeTargetResolver("box", target);

        var resolved = await Resolve.TargetAsync(new Uri("box:/backup"), "photos/a.jpg", new SourceInfo { Length = 3 }, [new FakeTargetResolver("other", new MemoryTarget()), resolver], Token);

        Assert.Same(target, resolved);
        Assert.Equal(("box:/backup", "photos/a.jpg", 3L), (resolver.Destination!.AbsoluteUri, resolver.Path, resolver.Info!.Length!.Value));
    }

    [Fact]
    public async Task A_destination_no_resolver_handles_is_unsupported_on_the_target_side()
    {
        var ex = await Assert.ThrowsAsync<TransferException>(async () => await Resolve.TargetAsync(new Uri("nowhere:/x"), "", null, [new FakeTargetResolver("box", new MemoryTarget())], Token));

        Assert.Equal(TransferErrorKind.Unsupported, ex.Kind);
        Assert.Equal(TransferSide.Target, ex.Side);
    }

    private sealed class FakeTargetResolver(string scheme, ITransferTarget target) : ITargetResolver
    {
        public Uri? Destination { get; private set; }
        public string? Path { get; private set; }
        public SourceInfo? Info { get; private set; }

        public bool CanResolve(Uri destination) => destination.Scheme == scheme;

        public ValueTask<ITransferTarget> ResolveAsync(Uri destination, string relativePath, SourceInfo? info, CancellationToken cancellationToken)
        {
            (Destination, Path, Info) = (destination, relativePath, info);
            return new ValueTask<ITransferTarget>(target);
        }
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("a", true)]
    [InlineData("album/02.mp3", true)]
    [InlineData("a b/c\\d:e", true)]
    [InlineData("/a", false)]
    [InlineData("a/", false)]
    [InlineData("a//b", false)]
    [InlineData("a/../b", false)]
    [InlineData("..", false)]
    [InlineData("./a", false)]
    public void Relative_paths_never_leave_their_place(string path, bool valid)
    {
        Assert.Equal(valid, RelativePath.IsValid(path));
        if (valid)
        {
            Assert.Equal(path, string.Join("/", RelativePath.Split(path)));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => RelativePath.Split(path));
            Assert.Throws<ArgumentException>(() => new ResolvedSource(Song1, path));
            Assert.Throws<ArgumentException>(() => new ResolvedReference(new Uri("next:page"), path));
        }
    }

    [Fact]
    public void A_resolved_source_always_has_a_source_and_a_reference_always_has_a_next_one()
    {
        Assert.Throws<ArgumentNullException>(() => new ResolvedSource(null!));
        Assert.Throws<ArgumentNullException>(() => new ResolvedReference(null!));
        Assert.Throws<ArgumentNullException>(() => new ResolvedSource(Song1, null!));

        Resolved item = new ResolvedReference(new Uri("next:page"), "art");
        Assert.Equal("art", item.Path);
        Assert.IsType<ResolvedReference>(item);
    }

    [Fact]
    public void Relative_paths_combine()
    {
        Assert.Equal("a/b/c", RelativePath.Combine("a", "b/c"));
        Assert.Equal("b", RelativePath.Combine("", "b"));
        Assert.Equal("a", RelativePath.Combine("a", ""));
    }
}
