using System.Reflection;
using System.Text.Json;
using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// Loads the plugins installed in a folder, each in a load context of its own, and collects what they register,
/// together with what the host builds in, in the order Naqel uses them.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each plugin has a folder of its own, with its <see cref="PluginManifest.FileName"/> and its assemblies. The
/// manifest is read first: a plugin built for other contracts than <see cref="PluginContract.Version"/> doesn't
/// load, and none of its code runs.</item>
/// <item>A plugin that can't load or fails to register is left out, with a <see cref="PluginFailure"/> saying why;
/// the others load.</item>
/// <item>Plugins load once, when Naqel starts. Installing, updating and removing plugins takes effect on the next
/// start (see <see cref="PluginCatalog"/>).</item>
/// </list>
/// </remarks>
public sealed class PluginHost
{
    private static readonly JsonSerializerOptions ManifestJson = new() { PropertyNameCaseInsensitive = true };

    private readonly PluginHostOptions _options;
    private readonly List<(PluginManifest Manifest, Action<IPluginRegistry> Register)> _builtIn = [];
    private readonly AccountsProxy _accounts = new();
    private readonly Dictionary<string, PluginSettings> _settings = new(StringComparer.Ordinal);

    /// <summary>
    /// A host for the plugins in <see cref="PluginHostOptions.PluginsDirectory"/>.
    /// </summary>
    public PluginHost(PluginHostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Adds what the host builds in, such as file resolvers or its storage locations, so it's ordered with what plugins
    /// bring. It registers before the installed plugins.
    /// </summary>
    /// <param name="id">A name for it, such as <c>naqel.builtin</c></param>
    /// <param name="register">Registers it</param>
    public void AddBuiltIn(string id, Action<IPluginRegistry> register)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(register);
        _builtIn.Add((new PluginManifest { Id = id, Name = id, Version = "", Contract = PluginContract.Version }, register));
    }

    /// <summary>
    /// Gives the plugins the user's accounts, once the manager that keeps them exists. Until then, a plugin that asks
    /// for an account while it registers gets none.
    /// </summary>
    public void Bind(IAccounts accounts) => _accounts.Bind(accounts ?? throw new ArgumentNullException(nameof(accounts)));

    /// <summary>
    /// Loads the installed plugins and what the host built in.
    /// </summary>
    public async Task<PluginSet> LoadAsync(CancellationToken cancellationToken = default)
    {
        var set = new PluginSet();
        foreach (var (manifest, register) in _builtIn)
        {
            var settings = await SettingsOfAsync(manifest.Id, cancellationToken).ConfigureAwait(false);
            var registry = new PluginSet.Registry(set, settings);
            register(registry);
            registry.Commit();
            set.Add(new LoadedPlugin(manifest, directory: null, settings));
        }

        if (!Directory.Exists(_options.PluginsDirectory))
        {
            return set;
        }
        // Before anything loads, while no plugin's files are in use
        PluginCatalog.ApplyPending(_options.PluginsDirectory);
        foreach (var directory in Directory.GetDirectories(_options.PluginsDirectory).Order(StringComparer.Ordinal))
        {
            // Folders starting with a dot belong to the catalog, such as plugins waiting to be installed
            if (Path.GetFileName(directory).StartsWith('.'))
            {
                continue;
            }
            await LoadPluginAsync(set, directory, cancellationToken).ConfigureAwait(false);
        }
        return set;
    }

    /// <summary>
    /// Changes a plugin's setting, keeping it in the settings store; the plugin hears of it through
    /// <see cref="IPluginSettings.Changed"/>.
    /// </summary>
    public async Task SetSettingAsync(string pluginId, string key, string? value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        ArgumentException.ThrowIfNullOrEmpty(key);
        await _options.Settings.SetAsync(pluginId, key, value, cancellationToken).ConfigureAwait(false);
        if (_settings.TryGetValue(pluginId, out var settings))
        {
            settings.Set(key, value);
        }
    }

    /// <summary>
    /// Reads the manifest in a plugin's folder.
    /// </summary>
    /// <exception cref="InvalidDataException">There's no manifest, or it isn't one</exception>
    public static PluginManifest ReadManifest(string directory)
    {
        var path = Path.Combine(directory, PluginManifest.FileName);
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"There's no {PluginManifest.FileName}");
        }
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), ManifestJson);
            if (manifest == null || manifest.Id.Length == 0 || manifest.Assembly.Length == 0)
            {
                throw new InvalidDataException($"{PluginManifest.FileName} needs an id and an assembly");
            }
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{PluginManifest.FileName} isn't valid: {ex.Message}", ex);
        }
    }

    private async Task LoadPluginAsync(PluginSet set, string directory, CancellationToken cancellationToken)
    {
        PluginManifest manifest;
        try
        {
            manifest = ReadManifest(directory);
        }
        catch (InvalidDataException ex)
        {
            set.Fail(new PluginFailure(directory, null, ex.Message));
            return;
        }
        if (manifest.Contract != PluginContract.Version)
        {
            set.Fail(new PluginFailure(directory, manifest, $"It's built for version {manifest.Contract} of the plugin contracts, and this Naqel has version {PluginContract.Version}"));
            return;
        }
        var assemblyPath = Path.GetFullPath(Path.Combine(directory, manifest.Assembly));
        if (!assemblyPath.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
        {
            set.Fail(new PluginFailure(directory, manifest, $"Its assembly {manifest.Assembly} isn't in its folder"));
            return;
        }

        INaqelPlugin plugin;
        try
        {
            var context = new PluginLoadContext(manifest.Id, assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var entries = assembly.GetExportedTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(INaqelPlugin).IsAssignableFrom(type))
                .ToList();
            if (entries.Count != 1)
            {
                set.Fail(new PluginFailure(directory, manifest, $"Its assembly has {entries.Count} public classes that implement INaqelPlugin, not one"));
                return;
            }
            plugin = (INaqelPlugin)Activator.CreateInstance(entries[0])!;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException or ReflectionTypeLoadException
                                       or TypeLoadException or MissingMethodException or TargetInvocationException)
        {
            set.Fail(new PluginFailure(directory, manifest, $"It didn't load: {ex.Message}"));
            return;
        }

        var settings = await SettingsOfAsync(manifest.Id, cancellationToken).ConfigureAwait(false);
        var registry = new PluginSet.Registry(set, settings);
        var data = Path.Combine(_options.DataDirectory, manifest.Id);
        Directory.CreateDirectory(data);
        try
        {
            plugin.Register(registry, new Context(manifest, settings, _accounts, data));
        }
#pragma warning disable CA1031 // A plugin's failure is reported, whatever it is, and the others still load
        catch (Exception ex)
#pragma warning restore CA1031
        {
            set.Fail(new PluginFailure(directory, manifest, $"It failed while registering: {ex.Message}"));
            return;
        }
        registry.Commit();
        set.Add(new LoadedPlugin(manifest, directory, settings));
    }

    private async Task<PluginSettings> SettingsOfAsync(string pluginId, CancellationToken cancellationToken)
    {
        var values = await _options.Settings.GetAllAsync(pluginId, cancellationToken).ConfigureAwait(false);
        var settings = new PluginSettings(values);
        _settings[pluginId] = settings;
        return settings;
    }

    private sealed record Context(PluginManifest Manifest, IPluginSettings Settings, IAccounts Accounts, string DataDirectory) : IPluginContext;

    // The accounts exist once the manager does, which needs the plugins first
    private sealed class AccountsProxy : IAccounts
    {
        private volatile IAccounts? _accounts;

        public void Bind(IAccounts accounts) => _accounts = accounts;

        public IReadOnlyList<AccountInfo> OfService(string service) => _accounts?.OfService(service) ?? [];

        public Task<string> GetTokenAsync(string service, string accountId, bool refresh, CancellationToken cancellationToken) =>
            (_accounts ?? throw new InvalidOperationException("The accounts aren't available yet")).GetTokenAsync(service, accountId, refresh, cancellationToken);
    }
}
