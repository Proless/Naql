namespace Naqel.Plugins;

/// <summary>
/// What a plugin gets while it registers: what it is, its settings, the user's accounts, and a folder of its own.
/// </summary>
public interface IPluginContext
{
    /// <summary>
    /// The plugin's manifest.
    /// </summary>
    PluginManifest Manifest { get; }

    /// <summary>
    /// The plugin's settings, as the user set them, or else as their definitions default them.
    /// </summary>
    IPluginSettings Settings { get; }

    /// <summary>
    /// The user's accounts for the services account providers sign in to, and their tokens.
    /// </summary>
    IAccounts Accounts { get; }

    /// <summary>
    /// A folder of the plugin's own for data it keeps, such as a cache, which stays when the plugin is updated.
    /// </summary>
    string DataDirectory { get; }
}
