using System.Text.Json;
using Naqel.Plugins;

namespace Naqel.Manager.Tests;

/// <summary>
/// Naqel.Http as a plugin: its build output loads as an installed plugin, behind site plugins.
/// </summary>
public sealed class HttpPluginTests : IDisposable
{
    private static readonly string Output = Path.Combine(AppContext.BaseDirectory, "http-plugin");

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "NaqelHttpPluginTests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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

    private string Install(string source, string folder)
    {
        var directory = Path.Combine(_temp, "plugins", folder);
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }
        return directory;
    }

    [Fact]
    public void Its_manifest_is_built_for_these_plugin_contracts()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(Output, PluginManifest.FileName)), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(PluginContract.Version, manifest.Contract);
        Assert.Equal("Naqel.Http.Plugin", manifest.Id);
        Assert.True(File.Exists(Path.Combine(Output, manifest.Assembly)));
    }

    [Fact]
    public async Task It_loads_and_resolves_http_links_after_site_plugins()
    {
        Install(Output, "http");
        var greeting = Install(Path.GetDirectoryName(Fakes.PluginPackages.TestPlugin)!, "greeting");
        File.WriteAllText(Path.Combine(greeting, PluginManifest.FileName),
            JsonSerializer.Serialize(new PluginManifest { Id = "Naqel.TestPlugin", Name = "Test", Version = "1.0.0", Contract = PluginContract.Version, Assembly = "Naqel.TestPlugin.dll" }));

        var set = await new PluginHost(new PluginHostOptions { PluginsDirectory = Path.Combine(_temp, "plugins"), DataDirectory = Path.Combine(_temp, "data") }).LoadAsync(Token);

        Assert.Empty(set.Failures);
        var http = set.Plugins.Single(plugin => plugin.Manifest.Id == "Naqel.Http.Plugin");
        Assert.StartsWith("Naqel.Http/", http.Settings.GetValue("userAgent"), StringComparison.Ordinal);
        Assert.Equal(["GreetingResolver", "HttpSourceResolver"], set.SourceResolvers.Select(resolver => resolver.GetType().Name));
        Assert.True(set.SourceResolvers[1].CanResolve(new Uri("https://example.com/file.zip")));
        Assert.True(Assert.Single(set.TargetResolvers).CanResolve(new Uri("https://example.com/upload/")));
    }
}
