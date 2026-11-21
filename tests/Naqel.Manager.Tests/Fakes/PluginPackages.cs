using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Naqel.Plugins;

namespace Naqel.Manager.Tests.Fakes;

/// <summary>
/// Writes NuGet packages into a folder, which NuGet reads as a source: plugin packages built from the test plugin, and
/// packages that are broken in the ways the catalog must refuse.
/// </summary>
public static class PluginPackages
{
    public static readonly string TestPlugin = Path.Combine(AppContext.BaseDirectory, "test-plugin", "Naqel.TestPlugin.dll");

    public static string Write(string feed, string id, string version, int contract = PluginContract.Version, bool tagged = true,
        bool manifest = true, string? manifestId = null, string assembly = "Naqel.TestPlugin.dll",
        IReadOnlyDictionary<string, byte[]>? extraFiles = null, bool includeAssembly = true)
    {
        Directory.CreateDirectory(feed);
        var path = Path.Combine(feed, $"{id}.{version}.nupkg");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var tags = tagged ? $"{PluginCatalog.PackageTag} naqel-contract-{contract}" : "library";
        Add(zip, $"{id}.nuspec", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <title>{id} plugin</title>
                <authors>Tests</authors>
                <description>A plugin for tests</description>
                <tags>{tags}</tags>
              </metadata>
            </package>
            """);
        if (manifest)
        {
            var content = new PluginManifest { Id = manifestId ?? id, Name = id, Version = version, Contract = contract, Assembly = assembly };
            Add(zip, PluginManifest.FileName, JsonSerializer.Serialize(content));
        }
        if (includeAssembly)
        {
            zip.CreateEntryFromFile(TestPlugin, "plugin/Naqel.TestPlugin.dll");
        }
        foreach (var (name, bytes) in extraFiles ?? new Dictionary<string, byte[]>())
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(bytes);
        }
        return path;
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }
}
