using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// A plugin Naqel loaded, or one built into the host, with what it registered.
/// </summary>
public sealed class LoadedPlugin
{
    internal LoadedPlugin(PluginManifest manifest, string? directory, PluginSettings settings)
    {
        Manifest = manifest;
        Directory = directory;
        Settings = settings;
    }

    /// <summary>
    /// What the plugin is.
    /// </summary>
    public PluginManifest Manifest { get; }

    /// <summary>
    /// Where it's installed, or <c>null</c> for what the host built in.
    /// </summary>
    public string? Directory { get; }

    /// <summary>
    /// Its settings, as the user set them.
    /// </summary>
    public IPluginSettings Settings { get; }

    /// <summary>
    /// What its settings are, for the UIs to show.
    /// </summary>
    public IReadOnlyList<SettingDefinition> SettingDefinitions => ((PluginSettings)Settings).Definitions;
}
