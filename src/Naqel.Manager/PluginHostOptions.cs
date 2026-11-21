namespace Naqel.Manager;

/// <summary>
/// Where the plugin host finds plugins and keeps what belongs to them.
/// </summary>
public sealed class PluginHostOptions
{
    /// <summary>
    /// The folder plugins are installed in, one folder each, named by the plugin's id.
    /// </summary>
    public required string PluginsDirectory { get; init; }

    /// <summary>
    /// The folder each plugin gets a folder of its own in, for data it keeps; it stays when a plugin is updated.
    /// </summary>
    public required string DataDirectory { get; init; }

    /// <summary>
    /// Where the plugins' settings are kept; in memory by default.
    /// </summary>
    public ISettingsStore Settings { get; init; } = new MemorySettingsStore();
}
