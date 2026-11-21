using Naqel.Core.Tests.Fakes;
using Naqel.Storage;

namespace Naqel.Core.Tests.Storage;

/// <summary>
/// The generic storage resolvers against a fake storage: files and folders, case, duplicate names, roots, and folder
/// copies between storages through resolvers alone.
/// </summary>
public sealed class StorageResolverTests
{
    private static readonly Uri Root = new("fake:/");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Content(int length, int seed)
    {
        var content = new byte[length];
        new Random(seed).NextBytes(content);
        return content;
    }

    private static async Task<List<ResolvedSource>> ResolveAsync(Uri reference, ISourceResolver resolver)
    {
        var items = new List<ResolvedSource>();
        await foreach (var item in Resolve.SourcesAsync(reference, [resolver], cancellationToken: Token))
        {
            items.Add(item);
        }
        return items;
    }

    [Fact]
    public async Task A_file_resolves_to_one_source_described_by_the_storage()
    {
        var storage = new FakeStorage();
        var content = Content(3_000, 1);
        storage.Add("docs/report.pdf", content);

        var item = Assert.Single(await ResolveAsync(new Uri("fake:/docs/report.pdf"), new StorageSourceResolver(Root, storage)));
        var target = new MemoryTarget();
        await Transfer.From(item.Source).To(target).RunAsync(cancellationToken: Token);

        Assert.Equal("", item.Path);
        Assert.Equal("report.pdf", item.Info!.Name);
        Assert.Equal(3_000, item.Info.Length);
        Assert.Equal(content, target.Session.Data);
    }

    [Fact]
    public async Task A_folder_resolves_to_its_files_with_their_paths_inside_it()
    {
        var storage = new FakeStorage();
        storage.Add("photos/a.jpg", [1]);
        storage.Add("photos/2026/trip/b.jpg", [2]);
        storage.Add("photos/2026/c.jpg", [3]);
        storage.Add("other/d.jpg", [4]);

        var recursive = await ResolveAsync(new Uri("fake:/photos"), new StorageSourceResolver(Root, storage));
        var flat = await ResolveAsync(new Uri("fake:/photos/"), new StorageSourceResolver(Root, storage, recursive: false));

        Assert.Equal(["2026/c.jpg", "2026/trip/b.jpg", "a.jpg"], recursive.Select(item => item.Path).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal(["a.jpg"], flat.Select(item => item.Path));
    }

    [Fact]
    public async Task A_storage_that_ignores_case_resolves_a_reference_in_any_case()
    {
        var storage = new FakeStorage(new StorageCapabilities { IgnoresCase = true });
        storage.Add("Photos/Trip/A.jpg", [1]);
        storage.Add("Photos/Trip/b.jpg", [2]);

        var items = await ResolveAsync(new Uri("fake:/PHOTOS/trip"), new StorageSourceResolver(Root, storage));

        // The paths inside the folder keep the case the storage has
        Assert.Equal(["A.jpg", "b.jpg"], items.Select(item => item.Path).OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Files_that_share_a_path_each_keep_a_place_of_their_own()
    {
        var drive = new FakeStorage(new StorageCapabilities { Ids = true, DuplicateNames = true });
        drive.Add("docs/notes.txt", [1]);
        drive.Add("docs/notes.txt", [2]);
        drive.Add("docs/notes.txt", [3]);
        drive.Add("docs/readme", [4]);
        drive.Add("docs/readme", [5]);
        var copy = new FakeStorage();

        await foreach (var item in Resolve.SourcesAsync(new Uri("fake:/docs"), [new StorageSourceResolver(Root, drive)], cancellationToken: Token))
        {
            var target = await Resolve.TargetAsync(new Uri("copy:/"), item.Path, item.Info, [new StorageTargetResolver(new Uri("copy:/"), copy)], cancellationToken: Token);
            await Transfer.From(item.Source).To(target).RunAsync(cancellationToken: Token);
        }

        Assert.Equal(["notes (2).txt", "notes (3).txt", "notes.txt", "readme", "readme (2)"], copy.Paths);
        Assert.Equal([1, 2, 3], new[] { "notes.txt", "notes (2).txt", "notes (3).txt" }.Select(path => (int)copy.Read(path)![0]).OrderBy(value => value));
    }

    [Fact]
    public async Task Nothing_at_a_reference_is_not_found()
    {
        var ex = await Assert.ThrowsAsync<TransferException>(() => ResolveAsync(new Uri("fake:/missing.txt"), new StorageSourceResolver(Root, new FakeStorage())));

        Assert.Equal(TransferErrorKind.NotFound, ex.Kind);
    }

    [Theory]
    [InlineData("fake:/media/a.txt", true)]
    [InlineData("fake:/media", true)]
    [InlineData("fake:/MEDIA/a.txt", false)]
    [InlineData("fake:/mediax/a.txt", false)]
    [InlineData("other:/media/a.txt", false)]
    [InlineData("fake://host/media/a.txt", false)]
    public void Only_references_under_the_root_are_resolved(string reference, bool resolved)
    {
        var resolver = new StorageSourceResolver(new Uri("fake:/media/"), new FakeStorage());

        Assert.Equal(resolved, resolver.CanResolve(new Uri(reference)));
    }

    [Fact]
    public void A_root_under_a_storage_that_ignores_case_takes_references_in_any_case()
    {
        var resolver = new StorageSourceResolver(new Uri("fake:/media/"), new FakeStorage(new StorageCapabilities { IgnoresCase = true }));

        Assert.True(resolver.CanResolve(new Uri("fake:/MEDIA/a.txt")));
        Assert.True(resolver.TryLocate(new Uri("fake:/Media/My%20Trip/a%2Bb.txt"), out _, out var path));
        Assert.Equal("My Trip/a+b.txt", path);
    }

    [Fact]
    public async Task A_destination_and_a_relative_path_write_a_file_inside_it()
    {
        var storage = new FakeStorage();
        var resolver = new StorageTargetResolver(Root, storage);

        var target = Assert.IsType<StorageTarget>(await resolver.ResolveAsync(new Uri("fake:/backups/"), "photos/a b.jpg", null, ResolveContext.Default, Token));
        await Transfer.From(new MemorySource([7])).To(target).RunAsync(cancellationToken: Token);

        Assert.Equal("backups/photos/a b.jpg", target.Path);
        Assert.Same(storage, target.Storage);
        Assert.Equal([7], storage.Read("backups/photos/a b.jpg"));
    }

    [Fact]
    public async Task A_single_file_goes_into_a_folder_with_its_own_name_and_otherwise_is_the_destination()
    {
        var storage = new FakeStorage();
        storage.Add("existing/x.bin", [0]);
        var resolver = new StorageTargetResolver(Root, storage);
        var info = new SourceInfo { Name = "report.pdf" };

        var intoSlash = (StorageTarget)await resolver.ResolveAsync(new Uri("fake:/inbox/"), "", info, ResolveContext.Default, Token);
        var intoFolder = (StorageTarget)await resolver.ResolveAsync(new Uri("fake:/existing"), "", info, ResolveContext.Default, Token);
        var intoRoot = (StorageTarget)await resolver.ResolveAsync(Root, "", info, ResolveContext.Default, Token);
        var asFile = (StorageTarget)await resolver.ResolveAsync(new Uri("fake:/renamed.pdf"), "", info, ResolveContext.Default, Token);

        Assert.Equal("inbox/report.pdf", intoSlash.Path);
        Assert.Equal("existing/report.pdf", intoFolder.Path);
        Assert.Equal("report.pdf", intoRoot.Path);
        Assert.Equal("renamed.pdf", asFile.Path);
    }

    [Fact]
    public async Task A_destination_keeps_what_it_writes_inside_it()
    {
        var resolver = new StorageTargetResolver(Root, new FakeStorage());

        await Assert.ThrowsAsync<ArgumentException>(async () => await resolver.ResolveAsync(new Uri("fake:/backups/"), "../escape.txt", null, ResolveContext.Default, Token));
        var unnamed = await Assert.ThrowsAsync<TransferException>(async () => await resolver.ResolveAsync(new Uri("fake:/backups/"), "", null, ResolveContext.Default, Token));
        Assert.Equal(TransferErrorKind.Unsupported, unnamed.Kind);
    }

    [Fact]
    public async Task A_folder_copies_from_one_storage_to_another_through_resolvers_alone()
    {
        var from = new FakeStorage(new StorageCapabilities { IgnoresCase = true, Ids = true });
        var to = new FakeStorage();
        var files = new Dictionary<string, byte[]>
        {
            ["Media/My Trip/a b.jpg"] = Content(5_000, 1),
            ["Media/My Trip/day 2/c#1.jpg"] = Content(10, 2),
            ["Media/notes.txt"] = Content(0, 3)
        };
        foreach (var (path, content) in files)
        {
            from.Add(path, content);
        }
        ISourceResolver[] sources = [new StorageSourceResolver(new Uri("drive:/"), from)];
        ITargetResolver[] targets = [new StorageTargetResolver(new Uri("nas://box/share/"), to)];

        await foreach (var item in Resolve.SourcesAsync(new Uri("drive:/media"), sources, cancellationToken: Token))
        {
            var target = await Resolve.TargetAsync(new Uri("nas://box/share/backup/"), item.Path, item.Info, targets, cancellationToken: Token);
            await Transfer.From(item.Source).To(target).RunAsync(cancellationToken: Token);
        }

        Assert.Equal(["backup/My Trip/a b.jpg", "backup/My Trip/day 2/c#1.jpg", "backup/notes.txt"], to.Paths);
        foreach (var (path, content) in files)
        {
            Assert.Equal(content, to.Read("backup/" + path["Media/".Length..]));
        }
    }
}
