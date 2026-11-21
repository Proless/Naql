namespace Naqel.Plugins;

/// <summary>
/// What a plugin is, from the <c>naqel-plugin.json</c> file in its package: read before the plugin loads, so Naqel can
/// refuse one built for other contracts without running its code.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>
    /// The file name of a manifest in a plugin's package and folder.
    /// </summary>
    public const string FileName = "naqel-plugin.json";

    /// <summary>The plugin's stable id, such as <c>Naqel.Plugins.Http</c>, usually its package id.</summary>
    public string Id { get; init; } = "";

    /// <summary>The plugin's name, to show.</summary>
    public string Name { get; init; } = "";

    /// <summary>The plugin's version, such as <c>1.2.0</c>.</summary>
    public string Version { get; init; } = "";

    /// <summary>The version of the plugin contracts the plugin was built for (<see cref="PluginContract.Version"/>).</summary>
    public int Contract { get; init; }

    /// <summary>The file name of the plugin's main assembly, such as <c>Naqel.Plugins.Http.dll</c>.</summary>
    public string Assembly { get; init; } = "";

    /// <summary>What the plugin does, to show.</summary>
    public string? Description { get; init; }

    /// <summary>Who made it, to show.</summary>
    public string? Authors { get; init; }
}
