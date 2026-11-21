using System.Globalization;
using System.Text.Json;
using Naqel.Plugins;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Naqel.Manager;

/// <summary>
/// Finds plugins in NuGet sources, such as nuget.org or a folder of packages, and installs, updates and removes them.
/// Changes take effect when Naqel starts next, since a loaded plugin's files can't be replaced while it runs.
/// </summary>
/// <remarks>
/// A plugin's package:
/// <list type="bullet">
/// <item>has the tags <see cref="PackageTag"/> and <c>naqel-contract-</c> followed by the
/// <see cref="PluginContract.Version"/> it's built for, so a search can tell which fit;</item>
/// <item>has its <see cref="PluginManifest.FileName"/> at the package's root, whose id is the package's id;</item>
/// <item>has its assemblies in a <c>plugin/</c> folder: its own, and those of every library it uses, since the catalog
/// installs one package, not its dependencies. Naqel brings <c>Naqel.Core</c> and <c>Naqel.Plugins</c> itself.</item>
/// </list>
/// </remarks>
public sealed class PluginCatalog
{
    /// <summary>
    /// The tag every plugin package has.
    /// </summary>
    public const string PackageTag = "naqel-plugin";

    private const string PendingFolder = ".pending";
    private const string TrashFolder = ".trash";
    private const string RemovalSuffix = ".remove";
    private const string PluginFolder = "plugin/";
    private static readonly string ContractTag = "naqel-contract-" + PluginContract.Version.ToString(CultureInfo.InvariantCulture);
    private static readonly JsonSerializerOptions ManifestJson = new() { PropertyNameCaseInsensitive = true };

    private readonly string _plugins;

    /// <summary>
    /// A catalog over <paramref name="sources"/>, such as <c>https://api.nuget.org/v3/index.json</c> or a folder,
    /// installing into <paramref name="pluginsDirectory"/>.
    /// </summary>
    public PluginCatalog(string pluginsDirectory, IEnumerable<string> sources)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginsDirectory);
        ArgumentNullException.ThrowIfNull(sources);
        _plugins = Path.GetFullPath(pluginsDirectory);
        Sources = [.. sources];
    }

    /// <summary>
    /// Where plugins come from, in order.
    /// </summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>
    /// The installed plugins, as their manifests describe them.
    /// </summary>
    public IReadOnlyList<PluginManifest> Installed
    {
        get
        {
            if (!Directory.Exists(_plugins))
            {
                return [];
            }
            var manifests = new List<PluginManifest>();
            foreach (var directory in Directory.GetDirectories(_plugins).Where(path => !Path.GetFileName(path).StartsWith('.')).Order(StringComparer.Ordinal))
            {
                try
                {
                    manifests.Add(PluginHost.ReadManifest(directory));
                }
                catch (InvalidDataException)
                {
                    // The plugin host explains it when it loads
                }
            }
            return manifests;
        }
    }

    /// <summary>
    /// The changes that take effect when Naqel starts next.
    /// </summary>
    public IReadOnlyList<PendingPluginChange> Pending
    {
        get
        {
            var pending = Path.Combine(_plugins, PendingFolder);
            if (!Directory.Exists(pending))
            {
                return [];
            }
            var installs = Directory.GetDirectories(pending).Select(directory => new PendingPluginChange(Path.GetFileName(directory), PluginHost.ReadManifest(directory).Version));
            var removals = Directory.GetFiles(pending, "*" + RemovalSuffix).Select(file => new PendingPluginChange(Path.GetFileName(file)[..^RemovalSuffix.Length], null));
            return [.. installs.Concat(removals).OrderBy(change => change.Id, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// The plugins the sources offer that match <paramref name="query"/>, newest version each, the first source's when
    /// several have one.
    /// </summary>
    public async Task<IReadOnlyList<CatalogEntry>> SearchAsync(string query = "", CancellationToken cancellationToken = default)
    {
        var entries = new List<CatalogEntry>();
        foreach (var source in Sources)
        {
            var search = await Repository.Factory.GetCoreV3(source).GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);
            if (search == null)
            {
                // A source that can't search, such as some private feeds, still installs by id
                continue;
            }
            var results = await search.SearchAsync(query, new SearchFilter(includePrerelease: true), 0, 100, NullLogger.Instance, cancellationToken).ConfigureAwait(false);
            foreach (var package in results)
            {
                var tags = (package.Tags ?? "").Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
                if (!tags.Contains(PackageTag, StringComparer.OrdinalIgnoreCase) || entries.Any(entry => entry.Id.Equals(package.Identity.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                entries.Add(new CatalogEntry(package.Identity.Id, package.Identity.Version.ToNormalizedString(), package.Title is { Length: > 0 } title ? title : package.Identity.Id,
                    package.Description, package.Authors, source, tags.Contains(ContractTag, StringComparer.OrdinalIgnoreCase)));
            }
        }
        return entries;
    }

    /// <summary>
    /// Installs a plugin, or updates it to another version; it takes effect when Naqel starts next.
    /// </summary>
    /// <param name="id">The plugin's package id</param>
    /// <param name="version">The version, or <c>null</c> for the newest</param>
    /// <param name="cancellationToken">Stops installing</param>
    /// <returns>The plugin's manifest</returns>
    /// <exception cref="KeyNotFoundException">No source has the package, or that version of it</exception>
    /// <exception cref="InvalidDataException">The package isn't a plugin this Naqel can load</exception>
    public async Task<PluginManifest> InstallAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        using var cache = new SourceCacheContext { NoCache = true };
        using var package = new MemoryStream();
        await DownloadAsync(id, version, package, cache, cancellationToken).ConfigureAwait(false);
        package.Position = 0;

        using var reader = new PackageArchiveReader(package);
        var manifest = ReadManifest(reader, id);
        var files = reader.GetFiles().Where(file => file.StartsWith(PluginFolder, StringComparison.OrdinalIgnoreCase) && !file.EndsWith('/')).ToList();
        if (files.Count == 0)
        {
            throw new InvalidDataException($"The package {id} has nothing in {PluginFolder}");
        }
        if (!files.Any(file => file.Equals(PluginFolder + manifest.Assembly, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"The package {id} doesn't have its assembly {manifest.Assembly} in {PluginFolder}");
        }

        // Staged whole, then moved into place on the next start; a half-written folder never loads
        var pending = Path.Combine(_plugins, PendingFolder);
        var staging = Path.Combine(pending, manifest.Id + ".staging");
        var target = Path.Combine(pending, manifest.Id);
        DeleteDirectory(staging);
        Directory.CreateDirectory(staging);
        foreach (var file in files)
        {
            var destination = Path.GetFullPath(Path.Combine(staging, file[PluginFolder.Length..]));
            if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                DeleteDirectory(staging);
                throw new InvalidDataException($"The package {id} has a file outside its plugin folder: {file}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = reader.GetStream(file);
            using var output = File.Create(destination);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(Path.Combine(staging, PluginManifest.FileName), JsonSerializer.Serialize(manifest), cancellationToken).ConfigureAwait(false);
        DeleteDirectory(target);
        File.Delete(Path.Combine(pending, manifest.Id + RemovalSuffix));
        Directory.Move(staging, target);
        return manifest;
    }

    /// <summary>
    /// Removes a plugin when Naqel starts next; its data folder stays. A pending install of it is dropped.
    /// </summary>
    /// <exception cref="KeyNotFoundException">There's no such plugin installed or about to be</exception>
    public void Remove(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var pending = Path.Combine(_plugins, PendingFolder);
        var installed = Directory.Exists(Path.Combine(_plugins, id));
        var staged = Directory.Exists(Path.Combine(pending, id));
        if (!installed && !staged)
        {
            throw new KeyNotFoundException($"The plugin {id} isn't installed");
        }
        DeleteDirectory(Path.Combine(pending, id));
        if (installed)
        {
            Directory.CreateDirectory(pending);
            File.WriteAllText(Path.Combine(pending, id + RemovalSuffix), "");
        }
    }

    /// <summary>
    /// Carries out the pending changes in <paramref name="pluginsDirectory"/>: before any plugin loads, which
    /// <see cref="PluginHost.LoadAsync"/> does by itself.
    /// </summary>
    /// <remarks>
    /// A plugin's old folder is moved aside before anything else, so a change either happens whole or not at all: one
    /// whose old folder is in use, such as by another running Naqel, stays pending for the next start, and Naqel starts
    /// with the plugin as it was. What was moved aside is deleted when it's no longer in use.
    /// </remarks>
    /// <returns>The changes that stayed pending</returns>
    public static IReadOnlyList<string> ApplyPending(string pluginsDirectory)
    {
        var trash = Path.Combine(pluginsDirectory, TrashFolder);
        EmptyTrash(trash);
        var pending = Path.Combine(pluginsDirectory, PendingFolder);
        if (!Directory.Exists(pending))
        {
            return [];
        }

        var left = new List<string>();
        foreach (var removal in Directory.GetFiles(pending, "*" + RemovalSuffix))
        {
            var id = Path.GetFileName(removal)[..^RemovalSuffix.Length];
            if (MoveAside(Path.Combine(pluginsDirectory, id), trash))
            {
                File.Delete(removal);
            }
            else
            {
                left.Add(id);
            }
        }
        foreach (var install in Directory.GetDirectories(pending).Where(path => !path.EndsWith(".staging", StringComparison.Ordinal)))
        {
            var id = Path.GetFileName(install);
            if (MoveAside(Path.Combine(pluginsDirectory, id), trash))
            {
                Directory.Move(install, Path.Combine(pluginsDirectory, id));
            }
            else
            {
                left.Add(id);
            }
        }
        EmptyTrash(trash);
        return left;
    }

    // Moving a folder fails while a file in it is in use, before anything in it changed
    private static bool MoveAside(string directory, string trash)
    {
        if (!Directory.Exists(directory))
        {
            return true;
        }
        try
        {
            Directory.CreateDirectory(trash);
            Directory.Move(directory, Path.Combine(trash, Path.GetFileName(directory) + "-" + Guid.NewGuid().ToString("N")));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void EmptyTrash(string trash)
    {
        if (!Directory.Exists(trash))
        {
            return;
        }
        foreach (var directory in Directory.GetDirectories(trash))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use; deleted on a later start
            }
        }
    }

    private async Task DownloadAsync(string id, string? version, Stream destination, SourceCacheContext cache, CancellationToken cancellationToken)
    {
        var wanted = version == null ? null : NuGetVersion.Parse(version);
        foreach (var source in Sources)
        {
            var find = await Repository.Factory.GetCoreV3(source).GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false);
            if (find == null)
            {
                continue;
            }
            var versions = (await find.GetAllVersionsAsync(id, cache, NullLogger.Instance, cancellationToken).ConfigureAwait(false)).ToList();
            var chosen = wanted == null ? versions.Max() : versions.FirstOrDefault(available => available == wanted);
            if (chosen != null && await find.CopyNupkgToStreamAsync(id, chosen, destination, cache, NullLogger.Instance, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        throw new KeyNotFoundException(version == null ? $"No source has the plugin {id}" : $"No source has version {version} of the plugin {id}");
    }

    private static PluginManifest ReadManifest(PackageArchiveReader reader, string id)
    {
        if (!reader.GetFiles().Contains(PluginManifest.FileName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The package {id} has no {PluginManifest.FileName}, so it isn't a Naqel plugin");
        }
        PluginManifest? manifest;
        using (var stream = reader.GetStream(reader.GetFiles().First(file => file.Equals(PluginManifest.FileName, StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                manifest = JsonSerializer.Deserialize<PluginManifest>(stream, ManifestJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"The package {id} has a {PluginManifest.FileName} that isn't valid: {ex.Message}", ex);
            }
        }
        if (manifest == null || !manifest.Id.Equals(reader.NuspecReader.GetId(), StringComparison.OrdinalIgnoreCase) || manifest.Assembly.Length == 0)
        {
            throw new InvalidDataException($"The package {id} has a {PluginManifest.FileName} with another id, or without an assembly");
        }
        if (manifest.Contract != PluginContract.Version)
        {
            throw new InvalidDataException($"The plugin {id} is built for version {manifest.Contract} of the plugin contracts, and this Naqel has version {PluginContract.Version}");
        }
        // The package's version is the one installed
        return manifest with { Version = reader.NuspecReader.GetVersion().ToNormalizedString() };
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
