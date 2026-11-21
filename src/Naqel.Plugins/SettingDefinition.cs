namespace Naqel.Plugins;

/// <summary>
/// One setting, described as data so both UIs can show it without code of the plugin's own.
/// </summary>
/// <param name="Key">The setting's stable key, such as <c>userAgent</c></param>
/// <param name="Title">Its name, to show</param>
/// <param name="Kind">What kind of value it holds</param>
public sealed record SettingDefinition(string Key, string Title, SettingKind Kind)
{
    /// <summary>What it does, to show.</summary>
    public string? Description { get; init; }

    /// <summary>Its value until the user sets one, or <c>null</c> for none.</summary>
    public string? Default { get; init; }

    /// <summary>The values a <see cref="SettingKind.Choice"/> setting can have.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>Whether it needs a value before it's used, such as a location's folder.</summary>
    public bool Required { get; init; }
}
