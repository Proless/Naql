namespace Naqel.Plugins;

/// <summary>
/// A plugin's settings as the user set them.
/// </summary>
public interface IPluginSettings
{
    /// <summary>
    /// The value of the setting <paramref name="key"/>: the user's, or else its definition's default, or <c>null</c>.
    /// </summary>
    string? GetValue(string key);

    /// <summary>
    /// Raised with a setting's key after the user changed it, for a plugin that applies changes while it runs.
    /// </summary>
    event Action<string>? Changed;
}
