namespace Naqel.Manager;

/// <summary>
/// Where settings are kept, by scope, such as a plugin's id. A host keeps the values of
/// <see cref="Plugins.SettingKind.Secret"/> settings encrypted.
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// Every value kept in <paramref name="scope"/>, by key.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(string scope, CancellationToken cancellationToken);

    /// <summary>
    /// Keeps a value, or deletes it when <paramref name="value"/> is <c>null</c>.
    /// </summary>
    Task SetAsync(string scope, string key, string? value, CancellationToken cancellationToken);
}
