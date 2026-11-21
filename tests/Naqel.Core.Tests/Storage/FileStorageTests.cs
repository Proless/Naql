using System.Runtime.InteropServices;
using Naqel.Core.Tests.Fakes;
using Naqel.Files;
using Naqel.Storage;

namespace Naqel.Core.Tests.Storage;

/// <summary>
/// A local folder as a storage: listing, describing, writing through the generic resolvers, deleting, moving,
/// copying, and paths that stay inside the folder.
/// </summary>
public sealed class FileStorageTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private string Root => _temp.File("root");

    private string Create(string relative, int length = 10)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private static async Task<List<StorageItem>> ListAsync(IStorage storage, string folder, bool recursive)
    {
        var items = new List<StorageItem>();
        await foreach (var item in storage.ListAsync(folder, recursive, Token))
        {
            items.Add(item);
        }
        return items;
    }

    [Fact]
    public async Task A_folder_lists_its_folders_and_files_in_name_order_without_unfinished_transfers()
    {
        Create("b.txt");
        Create("a/one.txt");
        Create("a/deep/two.txt");
        Create("c.txt" + FileTarget.PartialExtension);
        Create("c.txt" + FileCheckpointStore.Extension);
        var storage = new FileStorage(Root);

        var all = await ListAsync(storage, "", recursive: true);
        var top = await ListAsync(storage, "", recursive: false);

        Assert.Equal(["a", "a/deep", "a/deep/two.txt", "a/one.txt", "b.txt"], all.Select(item => item.Path));
        Assert.Equal([true, true, false, false, false], all.Select(item => item.IsFolder));
        Assert.Equal(["a", "b.txt"], top.Select(item => item.Path));
    }

    [Fact]
    public async Task A_file_is_described_the_way_a_file_source_describes_it()
    {
        var path = Create("docs/report.pdf", 1_234);
        var storage = new FileStorage(Root);

        var item = await storage.GetAsync("docs/report.pdf", Token);
        var info = await FileSource.At(path).GetInfoAsync(Token);

        Assert.NotNull(item);
        Assert.False(item.IsFolder);
        Assert.Equal("report.pdf", item.Name);
        Assert.Equal(1_234, item.Size);
        Assert.Equal(info.Version, item.Version);
        Assert.True((await storage.GetAsync("docs", Token))!.IsFolder);
        Assert.Null(await storage.GetAsync("docs/missing.pdf", Token));
        Assert.Null(await new FileStorage(_temp.File("nothing")).GetAsync("", Token));
    }

    [Fact]
    public async Task Paths_match_whatever_their_case_where_the_file_system_ignores_it()
    {
        Create("Photos/Trip/A.jpg");
        var storage = new FileStorage(Root);
        var ignores = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        var item = await storage.GetAsync("photos/trip/a.jpg", Token);

        Assert.Equal(ignores, storage.Capabilities.IgnoresCase);
        if (ignores)
        {
            // Found, with the case it has on disk
            Assert.Equal("Photos/Trip/A.jpg", item!.Path);
        }
        else
        {
            Assert.Null(item);
        }
    }

    [Fact]
    public async Task A_folder_copies_into_the_storage_through_the_generic_resolvers_with_safe_names()
    {
        var from = new FileStorage(_temp.File("from"));
        Directory.CreateDirectory(_temp.File("from"));
        File.WriteAllBytes(Path.Combine(_temp.File("from"), "a.txt"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(_temp.File("from"), "sub"));
        File.WriteAllBytes(Path.Combine(_temp.File("from"), "sub", "b.txt"), [4]);
        var to = new FileStorage(Root);
        var fromRoot = new Uri(_temp.File("from") + Path.DirectorySeparatorChar);
        var toRoot = new Uri(Root + Path.DirectorySeparatorChar);

        await foreach (var item in Resolve.SourcesAsync(fromRoot, [new StorageSourceResolver(fromRoot, from)], cancellationToken: Token))
        {
            var target = await Resolve.TargetAsync(new Uri(toRoot, "backup/"), item.Path, item.Info, [new StorageTargetResolver(toRoot, to)], cancellationToken: Token);
            await Transfer.From(item.Source).To(target).RunAsync(cancellationToken: Token);
        }
        var unsafeName = await new StorageTargetResolver(toRoot, to).ResolveAsync(toRoot, "what? a:name.txt", null, ResolveContext.Default, Token);
        await Transfer.From(new MemorySource([9])).To(unsafeName).RunAsync(cancellationToken: Token);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(Root, "backup", "a.txt")));
        Assert.Equal([4], File.ReadAllBytes(Path.Combine(Root, "backup", "sub", "b.txt")));
        // A name the file system can't take is written under a safe one
        var written = Assert.Single(Directory.GetFiles(Root));
        Assert.DoesNotContain(':', Path.GetFileName(written));
    }

    [Fact]
    public async Task Files_and_folders_can_be_deleted_moved_and_copied()
    {
        Create("a.txt", 3);
        Create("folder/b.txt", 4);
        var storage = new FileStorage(Root);

        var copied = await storage.CopyAsync((await storage.GetAsync("a.txt", Token))!, "copies/a.txt", Token);
        var moved = await storage.MoveAsync((await storage.GetAsync("folder", Token))!, "renamed", Token);
        await storage.DeleteAsync((await storage.GetAsync("a.txt", Token))!, Token);

        Assert.Equal("copies/a.txt", copied.Path);
        Assert.Equal(3, copied.Size);
        Assert.Equal("renamed", moved.Path);
        Assert.True(moved.IsFolder);
        Assert.True(File.Exists(Path.Combine(Root, "renamed", "b.txt")));
        Assert.False(File.Exists(Path.Combine(Root, "a.txt")));
        Assert.Equal(["copies", "copies/a.txt", "renamed", "renamed/b.txt"], (await ListAsync(storage, "", recursive: true)).Select(item => item.Path));
    }

    [Fact]
    public async Task Moving_onto_something_fails_and_the_storages_own_folder_cant_be_deleted()
    {
        Create("a.txt");
        Create("b.txt");
        var storage = new FileStorage(Root);

        var taken = await Assert.ThrowsAsync<TransferException>(async () => await storage.MoveAsync((await storage.GetAsync("a.txt", Token))!, "b.txt", Token));
        var root = await Assert.ThrowsAsync<TransferException>(async () => await storage.DeleteAsync((await storage.GetAsync("", Token))!, Token));

        Assert.Equal(TransferErrorKind.AlreadyExists, taken.Kind);
        Assert.Equal(TransferErrorKind.Unsupported, root.Kind);
        Assert.True(File.Exists(Path.Combine(Root, "a.txt")));
    }

    [Fact]
    public async Task Paths_stay_inside_the_folder()
    {
        Create("a.txt");
        var storage = new FileStorage(Root);

        await Assert.ThrowsAsync<ArgumentException>(() => storage.GetAsync("../outside.txt", Token));
        Assert.Throws<ArgumentException>(() => storage.OpenWrite("sub/../../outside.txt", WriteMode.Overwrite, null));
    }
}
