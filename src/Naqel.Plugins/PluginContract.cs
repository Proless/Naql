namespace Naqel.Plugins;

/// <summary>
/// The version of the plugin contracts. A plugin's manifest names the version it was built for, and Naqel loads only
/// plugins built for the version it has; a version changes when a change to the contracts would break plugins.
/// </summary>
public static class PluginContract
{
    /// <summary>
    /// The version of the contracts in this assembly.
    /// </summary>
    public const int Version = 1;
}
