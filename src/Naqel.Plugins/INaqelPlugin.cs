namespace Naqel.Plugins;

/// <summary>
/// A plugin's entry point: Naqel creates it with its parameterless constructor when it loads the plugin, and the plugin
/// registers what it brings: resolvers, engines, hooks, account providers and settings.
/// </summary>
/// <remarks>
/// A plugin's main assembly has exactly one public class that implements this interface. Registering runs once, when
/// Naqel starts; changing a plugin's settings takes effect when it starts the next time, unless the plugin follows
/// <see cref="IPluginSettings.Changed"/> itself.
/// </remarks>
public interface INaqelPlugin
{
    /// <summary>
    /// Registers what the plugin brings.
    /// </summary>
    /// <param name="registry">Where to register it</param>
    /// <param name="context">The plugin's manifest, settings, accounts and a folder of its own</param>
    void Register(IPluginRegistry registry, IPluginContext context);
}
