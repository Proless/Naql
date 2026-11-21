namespace Naqel.Manager;

/// <summary>
/// A change to the installed plugins that takes effect when Naqel starts next.
/// </summary>
/// <param name="Id">The plugin's id</param>
/// <param name="Version">The version that will be installed, or <c>null</c> when the plugin will be removed</param>
public sealed record PendingPluginChange(string Id, string? Version)
{
    /// <summary>
    /// Whether the plugin will be removed.
    /// </summary>
    public bool IsRemoval => Version == null;
}
