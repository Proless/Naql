namespace Naqel.Manager;

/// <summary>
/// Where the manager keeps secrets, such as account tokens and secret plugin settings. A host keeps them encrypted:
/// with the OS's secure storage on the desktop, and with data protection keys on the server.
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// The secret under <paramref name="key"/> in <paramref name="scope"/>, such as an account, or <c>null</c>.
    /// </summary>
    Task<string?> GetAsync(string scope, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Keeps a secret, or deletes it when <paramref name="value"/> is <c>null</c>.
    /// </summary>
    Task SetAsync(string scope, string key, string? value, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every secret in <paramref name="scope"/>.
    /// </summary>
    Task DeleteScopeAsync(string scope, CancellationToken cancellationToken);
}
