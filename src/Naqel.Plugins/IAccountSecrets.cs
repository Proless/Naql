namespace Naqel.Plugins;

/// <summary>
/// The secrets Naqel keeps for one account, such as a refresh token, encrypted where the host keeps credentials.
/// </summary>
public interface IAccountSecrets
{
    /// <summary>
    /// The secret kept under <paramref name="key"/>, or <c>null</c>.
    /// </summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Keeps <paramref name="value"/> under <paramref name="key"/>, or deletes it when it's <c>null</c>, such as a
    /// refresh token the service replaced.
    /// </summary>
    Task SetAsync(string key, string? value, CancellationToken cancellationToken);
}
