namespace Naqel.Plugins;

/// <summary>
/// The user's accounts, as plugins use them: to list them, and to get tokens through their providers.
/// </summary>
public interface IAccounts
{
    /// <summary>
    /// The accounts for <paramref name="service"/>, in the order they were signed in to.
    /// </summary>
    IReadOnlyList<AccountInfo> OfService(string service);

    /// <summary>
    /// A token for an account: the current one, or with <paramref name="refresh"/> a fresh one, as Naqel.Http's
    /// <c>BearerTokenHandler</c> asks for them.
    /// </summary>
    /// <exception cref="KeyNotFoundException">There's no such account</exception>
    /// <exception cref="TransferException">The account needs signing in again (<see cref="TransferErrorKind.Unauthorized"/>)</exception>
    Task<string> GetTokenAsync(string service, string accountId, bool refresh, CancellationToken cancellationToken);
}
