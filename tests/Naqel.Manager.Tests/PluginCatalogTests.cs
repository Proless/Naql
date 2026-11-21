using Naqel.Manager.Tests.Fakes;
using Naqel.Plugins;

namespace Naqel.Manager.Tests;

/// <summary>
/// The plugin catalog against a folder of NuGet packages: searching, installing, updating and removing, which take
/// effect when the plugin host loads next, and refusing packages that aren't plugins this Naqel can load.
/// </summary>
public sealed class PluginCatalogTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "NaqelCatalogTests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Feed => Path.Combine(_temp, "feed");

    private string Plugins => Path.Combine(_temp, "plugins");

    private string Data => Path.Combine(_temp, "data");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A loaded plugin's assembly stays open until the process ends
        }
    }

    private PluginCatalog Catalog() => new(Plugins, [Feed]);

    private Task<PluginSet> LoadAsync() => new PluginHost(new PluginHostOptions { PluginsDirectory = Plugins, DataDirectory = Data }).LoadAsync(Token);

    [Fact]
    public async Task A_search_lists_the_plugins_of_a_source_and_whether_they_fit()
    {
        PluginPackages.Write(Feed, "Acme.Greeting", "1.0.0");
        PluginPackages.Write(Feed, "Acme.Greeting", "1.2.0");
        PluginPackages.Write(Feed, "Acme.Future", "2.0.0", contract: PluginContract.Version + 1);
        PluginPackages.Write(Feed, "Acme.Library", "1.0.0", tagged: false);

        var all = await Catalog().SearchAsync(cancellationToken: Token);
        var some = await Catalog().SearchAsync("greeting", Token);

        Assert.Equal(["Acme.Future", "Acme.Greeting"], all.Select(entry => entry.Id).Order(StringComparer.Ordinal));
        var greeting = all.Single(entry => entry.Id == "Acme.Greeting");
        Assert.Equal("1.2.0", greeting.Version);
        Assert.True(greeting.Compatible);
        Assert.False(all.Single(entry => entry.Id == "Acme.Future").Compatible);
        Assert.Equal(Feed, greeting.Source);
        Assert.Equal(["Acme.Greeting"], some.Select(entry => entry.Id));
    }

    [Fact]
    public async Task An_installed_plugin_loads_on_the_next_start()
    {
        PluginPackages.Write(Feed, "Acme.Greeting", "1.0.0");
        var catalog = Catalog();

        var manifest = await catalog.InstallAsync("Acme.Greeting", cancellationToken: Token);

        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal([new PendingPluginChange("Acme.Greeting", "1.0.0")], catalog.Pending);
        Assert.Empty(catalog.Installed);

        var set = await LoadAsync();

        Assert.Equal(["Acme.Greeting"], set.Plugins.Select(plugin => plugin.Manifest.Id));
        Assert.Single(set.SourceResolvers);
        Assert.Empty(catalog.Pending);
        Assert.Equal(["Acme.Greeting"], catalog.Installed.Select(installed => installed.Id));
    }

    [Fact]
    public async Task Updating_a_plugin_replaces_it_and_keeps_its_data()
    {
        PluginPackages.Write(Feed, "Acme.Greeting", "1.0.0");
        PluginPackages.Write(Feed, "Acme.Greeting", "2.0.0");
        var catalog = Catalog();
        await catalog.InstallAsync("Acme.Greeting", "1.0.0", Token);
        // Installed as on a start, without loading it: a loaded plugin's files stay in use until its process ends
        PluginCatalog.ApplyPending(Plugins);
        Assert.Equal("1.0.0", Assert.Single(catalog.Installed).Version);
        var kept = Path.Combine(Data, "Acme.Greeting", "cache.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllText(kept, "mine");

        await catalog.InstallAsync("Acme.Greeting", cancellationToken: Token);
        var set = await LoadAsync();

        Assert.Equal("2.0.0", Assert.Single(set.Plugins).Manifest.Version);
        Assert.Equal("mine", File.ReadAllText(kept));
    }

    [Fact]
    public async Task A_removed_plugin_is_gone_on_the_next_start_and_its_data_stays()
    {
        PluginPackages.Write(Feed, "Acme.Greeting", "1.0.0");
        var catalog = Catalog();
        await catalog.InstallAsync("Acme.Greeting", cancellationToken: Token);
        PluginCatalog.ApplyPending(Plugins);
        Directory.CreateDirectory(Path.Combine(Data, "Acme.Greeting"));

        catalog.Remove("Acme.Greeting");

        Assert.Equal([new PendingPluginChange("Acme.Greeting", null)], catalog.Pending);
        Assert.True(catalog.Pending[0].IsRemoval);
        var set = await LoadAsync();
        Assert.Empty(set.Plugins);
        Assert.Empty(catalog.Installed);
        Assert.True(Directory.Exists(Path.Combine(Data, "Acme.Greeting")));
        Assert.Throws<KeyNotFoundException>(() => catalog.Remove("Acme.Greeting"));
    }

    [Fact]
    public async Task A_change_to_a_plugin_whose_files_are_in_use_waits_for_a_later_start_and_leaves_it_whole()
    {
        // Only Windows keeps files that are in use from being moved
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Files in use only block moves on Windows");
        PluginPackages.Write(Feed, "Acme.Greeting", "1.0.0");
        PluginPackages.Write(Feed, "Acme.Greeting", "2.0.0");
        var catalog = Catalog();
        await catalog.InstallAsync("Acme.Greeting", "1.0.0", Token);
        PluginCatalog.ApplyPending(Plugins);
        await catalog.InstallAsync("Acme.Greeting", "2.0.0", Token);

        IReadOnlyList<string> left;
        using (File.Open(Path.Combine(Plugins, "Acme.Greeting", "Naqel.TestPlugin.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            left = PluginCatalog.ApplyPending(Plugins);
        }

        Assert.Equal(["Acme.Greeting"], left);
        Assert.Equal("1.0.0", Assert.Single(catalog.Installed).Version);
        Assert.True(File.Exists(Path.Combine(Plugins, "Acme.Greeting", "Naqel.TestPlugin.dll")));
        Assert.Equal([new PendingPluginChange("Acme.Greeting", "2.0.0")], catalog.Pending);

        Assert.Empty(PluginCatalog.ApplyPending(Plugins));
        Assert.Equal("2.0.0", Assert.Single(catalog.Installed).Version);
    }

    [Fact]
    public async Task Removing_a_plugin_that_waits_to_be_installed_drops_the_install()
    {
        PluginPackages.Write(Feed, "Acme.Greeting", "1.0.0");
        var catalog = Catalog();
        await catalog.InstallAsync("Acme.Greeting", cancellationToken: Token);

        catalog.Remove("Acme.Greeting");

        Assert.Empty(catalog.Pending);
        Assert.Empty((await LoadAsync()).Plugins);
    }

    [Theory]
    [InlineData("no-manifest")]
    [InlineData("other-contract")]
    [InlineData("other-id")]
    [InlineData("no-assembly")]
    [InlineData("escaping-file")]
    public async Task A_package_that_isnt_a_plugin_this_Naqel_can_load_is_refused(string problem)
    {
        PluginPackages.Write(Feed, "Acme.Bad", "1.0.0",
            manifest: problem != "no-manifest",
            contract: problem == "other-contract" ? PluginContract.Version + 1 : PluginContract.Version,
            manifestId: problem == "other-id" ? "Acme.Other" : null,
            includeAssembly: problem != "no-assembly",
            extraFiles: problem == "escaping-file" ? new Dictionary<string, byte[]> { ["plugin/../../evil.dll"] = [1] } : null);
        var catalog = Catalog();

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.InstallAsync("Acme.Bad", cancellationToken: Token));

        Assert.Empty(catalog.Pending);
        Assert.False(File.Exists(Path.Combine(_temp, "evil.dll")));
        Assert.False(File.Exists(Path.Combine(Plugins, "evil.dll")));
    }

    [Fact]
    public async Task A_package_or_version_no_source_has_isnt_found()
    {
        PluginPackages.Write(Feed, "Acme.Greeting", "1.0.0");
        var catalog = Catalog();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => catalog.InstallAsync("Acme.Missing", cancellationToken: Token));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => catalog.InstallAsync("Acme.Greeting", "9.0.0", Token));
    }
}
