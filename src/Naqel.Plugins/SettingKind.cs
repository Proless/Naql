namespace Naqel.Plugins;

/// <summary>
/// What kind of value a setting holds, which decides how the UIs ask for it.
/// </summary>
public enum SettingKind
{
    /// <summary>Text, such as a folder or a name.</summary>
    Text,

    /// <summary>Text the UIs hide and Naqel keeps encrypted, such as an API key.</summary>
    Secret,

    /// <summary>A whole number.</summary>
    Number,

    /// <summary><c>true</c> or <c>false</c>.</summary>
    Boolean,

    /// <summary>One of <see cref="SettingDefinition.Choices"/>.</summary>
    Choice
}
