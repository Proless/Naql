using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// A plugin that's installed but didn't load, and why, for the UIs to show.
/// </summary>
/// <param name="Directory">Where it's installed</param>
/// <param name="Manifest">Its manifest, when it could be read</param>
/// <param name="Reason">Why it didn't load, such as being built for other contracts</param>
public sealed record PluginFailure(string Directory, PluginManifest? Manifest, string Reason);
