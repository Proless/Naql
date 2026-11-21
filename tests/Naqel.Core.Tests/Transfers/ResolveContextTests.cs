using Naqel.Files;
using Naqel.Core.Tests.Fakes;

namespace Naqel.Core.Tests.Transfers;

/// <summary>
/// The choices for one resolution: typed options every resolver along the way can read, and a filter on the sources.
/// </summary>
public sealed class ResolveContextTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private sealed record Quality(string Name);

    private sealed record Account(string Name);

    /// <summary>
    /// Records the context it got, and yields what <paramref name="resolve"/> gives.
    /// </summary>
    private sealed class Recording(string scheme, Func<Uri, ResolveContext, IEnumerable<Resolved>> resolve) : ISourceResolver
    {
        public List<ResolveContext> Contexts { get; } = [];

        public bool CanResolve(Uri reference) => reference.Scheme == scheme;

        public async IAsyncEnumerable<Resolved> ResolveAsync(Uri reference, ResolveContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            foreach (var item in resolve(reference, context))
            {
                await Task.Yield();
                yield return item;
            }
        }
    }

    private static async Task<List<ResolvedSource>> AllAsync(Uri reference, ResolveContext? context, params ISourceResolver[] resolvers)
    {
        var all = new List<ResolvedSource>();
        await foreach (var item in Resolve.SourcesAsync(reference, resolvers, context, cancellationToken: Token))
        {
            all.Add(item);
        }
        return all;
    }

    [Fact]
    public async Task Every_resolver_along_the_way_gets_the_same_options()
    {
        var shortener = new Recording("short", (_, _) => [new ResolvedReference(new Uri("video:1"))]);
        var video = new Recording("video", (_, context) => [new ResolvedSource(new MemorySource([1]), $"{context.Get<Quality>()?.Name ?? "default"}.mp4")]);
        var context = ResolveContext.Default.With(new Quality("720p")).With(new Account("work"));

        var all = await AllAsync(new Uri("short:abc"), context, shortener, video);

        Assert.Equal("720p.mp4", Assert.Single(all).Path);
        Assert.Same(context, Assert.Single(shortener.Contexts));
        Assert.Same(context, Assert.Single(video.Contexts));
        Assert.Equal("work", video.Contexts[0].Get<Account>()!.Name);
    }

    [Fact]
    public async Task Without_a_context_resolvers_get_the_default_one()
    {
        var resolver = new Recording("plain", (_, _) => [new ResolvedSource(new MemorySource([1]))]);

        await AllAsync(new Uri("plain:x"), null, resolver);

        Assert.Same(ResolveContext.Default, Assert.Single(resolver.Contexts));
    }

    [Fact]
    public void A_context_never_changes_and_each_type_has_one_set_of_options()
    {
        var first = ResolveContext.Default.With(new Quality("480p"));
        var second = first.With(new Quality("1080p"));

        Assert.Null(ResolveContext.Default.Get<Quality>());
        Assert.Equal("480p", first.Get<Quality>()!.Name);
        Assert.Equal("1080p", second.Get<Quality>()!.Name);
        Assert.Null(second.Get<Account>());
    }

    [Fact]
    public async Task The_filter_chooses_by_the_final_path_and_keeps_the_options()
    {
        var album = new Recording("album", (reference, _) => reference.AbsoluteUri == "album:1"
            ? [new ResolvedSource(new MemorySource([1]), "01.mp3"), new ResolvedReference(new Uri("album:art"), "art")]
            : [new ResolvedSource(new MemorySource([2]), "cover.jpg"), new ResolvedSource(new MemorySource([3]), "back.jpg")]);
        var context = ResolveContext.Default.With(new Quality("best")).WithFilter(item => item.Path.StartsWith("art/", StringComparison.Ordinal));

        var all = await AllAsync(new Uri("album:1"), context, album);

        Assert.Equal(["art/cover.jpg", "art/back.jpg"], all.Select(item => item.Path));
        Assert.Equal("best", context.Get<Quality>()!.Name);
    }

    [Fact]
    public async Task A_target_resolver_gets_the_context()
    {
        var destination = new Uri(_temp.File("out") + Path.DirectorySeparatorChar);
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(_temp.File("out")).FullName, "a.bin"), [9]);
        var context = ResolveContext.Default.With(new FileResolveOptions { Conflict = FileConflict.Fail });

        var target = await Resolve.TargetAsync(destination, "a.bin", null, [new FileTargetResolver()], context, Token);
        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => Transfer.From(new MemorySource([1])).To(target).RunAsync(cancellationToken: Token));

        Assert.Equal(TransferErrorKind.AlreadyExists, ex.Kind);
    }

    [Fact]
    public async Task File_options_override_a_source_resolvers_own_setting_for_one_call()
    {
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(_temp.File("in")).FullName, "a.txt"), [1]);
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(_temp.File("in/sub")).FullName, "b.txt"), [2]);
        var reference = new Uri(_temp.File("in"));
        var resolver = new FileSourceResolver(recursive: true);

        var flat = await AllAsync(reference, ResolveContext.Default.With(new FileResolveOptions { Recursive = false }), resolver);
        var deep = await AllAsync(reference, null, resolver);

        Assert.Equal(["a.txt"], flat.Select(item => item.Path));
        Assert.Equal(2, deep.Count);
    }
}
