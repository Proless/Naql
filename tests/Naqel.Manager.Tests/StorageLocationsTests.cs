using Naqel.Files;
using Naqel.Storage;

namespace Naqel.Manager.Tests;

/// <summary>
/// Named storage locations: names, references, and resolving sources and targets in them.
/// </summary>
public sealed class StorageLocationsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "NaqelLocationTests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written
        }
    }

    [Fact]
    public void Locations_are_added_found_and_removed_by_name()
    {
        var locations = new StorageLocations();
        var storage = new FileStorage(_temp);

        var media = locations.Add("media", storage);
        locations.Add("backups-2", storage, WriteMode.Fail);

        Assert.Equal(["media", "backups-2"], locations.All.Select(location => location.Name));
        Assert.Same(storage, locations.Find("media")!.Storage);
        Assert.Equal(new Uri("location://media/"), media.Uri);
        Assert.Equal(WriteMode.Fail, locations.Find("backups-2")!.Mode);
        Assert.True(locations.Remove("media"));
        Assert.False(locations.Remove("media"));
        Assert.Null(locations.Find("media"));
    }

    [Theory]
    [InlineData("Media")]
    [InlineData("my_media")]
    [InlineData("-media")]
    [InlineData("media-")]
    [InlineData("")]
    [InlineData("a b")]
    public void A_name_must_be_lowercase_letters_digits_and_hyphens(string name)
    {
        Assert.Throws<ArgumentException>(() => new StorageLocations().Add(name, new FileStorage(_temp)));
    }

    [Fact]
    public void Two_locations_cant_share_a_name()
    {
        var locations = new StorageLocations();
        locations.Add("media", new FileStorage(_temp));

        Assert.Throws<ArgumentException>(() => locations.Add("media", new FileStorage(_temp)));
    }

    [Fact]
    public void References_name_a_path_in_a_location_with_each_segment_escaped()
    {
        Assert.Equal("location://media/", StorageLocations.UriOf("media").AbsoluteUri);
        Assert.Equal("location://media/My%20Trip/day%202/", StorageLocations.UriOf("media", "My Trip/day 2").AbsoluteUri);
        Assert.Equal("location://media/a%23b.txt", StorageLocations.UriOf("media", "a#b.txt", folder: false).AbsoluteUri);
        Assert.Throws<ArgumentException>(() => StorageLocations.UriOf("media", "../escape"));
    }

    [Fact]
    public async Task A_location_resolves_its_files_and_destinations_and_knows_their_paths()
    {
        var locations = new StorageLocations();
        var storage = new FileStorage(_temp);
        locations.Add("media", storage);
        Directory.CreateDirectory(Path.Combine(_temp, "My Trip"));
        File.WriteAllBytes(Path.Combine(_temp, "My Trip", "a.jpg"), [1, 2]);

        var sources = new List<ResolvedSource>();
        await foreach (var item in Resolve.SourcesAsync(StorageLocations.UriOf("media", "My Trip"), [locations], cancellationToken: Token))
        {
            sources.Add(item);
        }
        var target = (StorageTarget)await Resolve.TargetAsync(StorageLocations.UriOf("media", "copies"), "a.jpg", null, [locations], cancellationToken: Token);
        await Transfer.From(sources[0].Source).To(target).RunAsync(cancellationToken: Token);

        Assert.Equal("a.jpg", Assert.Single(sources).Path);
        Assert.Equal("copies/a.jpg", target.Path);
        Assert.Equal([1, 2], File.ReadAllBytes(Path.Combine(_temp, "copies", "a.jpg")));
        Assert.True(locations.TryLocate(StorageLocations.UriOf("media", "My Trip"), out var located, out var path));
        Assert.Same(storage, located);
        Assert.Equal("My Trip", path);
    }

    [Fact]
    public async Task Every_location_reference_is_taken_and_one_to_no_location_is_not_found()
    {
        var locations = new StorageLocations();
        locations.Add("media", new FileStorage(_temp));

        Assert.True(locations.CanResolve(new Uri("location://media/a.txt")));
        Assert.True(locations.CanResolve(new Uri("location://MEDIA/a.txt")));
        Assert.True(locations.CanResolve(new Uri("location://other/a.txt")));
        Assert.False(locations.CanResolve(new Uri("file:///media/a.txt")));
        Assert.False(locations.TryLocate(new Uri("location://other/a.txt"), out _, out _));

        var ex = await Assert.ThrowsAsync<TransferException>(async () =>
            await Resolve.TargetAsync(new Uri("location://other/"), "a.txt", null, [locations], cancellationToken: Token));
        Assert.Equal(TransferErrorKind.NotFound, ex.Kind);
        Assert.Contains("other", ex.Message, StringComparison.Ordinal);
    }
}
