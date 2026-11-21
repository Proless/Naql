namespace Naqel.Plugins;

/// <summary>
/// Signs in to one service and keeps its credentials fresh, so the plugins for that service get tokens without asking
/// the user each time.
/// </summary>
/// <remarks>
/// Signing in asks the user through <see cref="IInteraction"/>, such as for a password, or to open the service's sign-in
/// page in the browser. What the account needs later, such as a refresh token, goes into its
/// <see cref="IAccountSecrets"/>; Naqel keeps it encrypted.
/// </remarks>
public interface IAccountProvider
{
    /// <summary>
    /// The service's stable name, such as <c>dropbox</c>.
    /// </summary>
    string Service { get; }

    /// <summary>
    /// The service's name, to show.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// Signs in to a new account, asking the user what's needed.
    /// </summary>
    /// <param name="interaction">Asks the user</param>
    /// <param name="secrets">Where to keep the account's secrets, once its id is known</param>
    /// <param name="cancellationToken">Stops signing in</param>
    /// <returns>The account</returns>
    /// <exception cref="TransferException">Signing in failed, such as with wrong credentials (<see cref="TransferErrorKind.Unauthorized"/>)</exception>
    Task<AccountInfo> SignInAsync(IInteraction interaction, Func<string, IAccountSecrets> secrets, CancellationToken cancellationToken);

    /// <summary>
    /// A token for the account: the current one, or with <paramref name="refresh"/> a fresh one because the current one
    /// was rejected.
    /// </summary>
    /// <exception cref="TransferException">The account can't get a token any more and needs signing in again
    /// (<see cref="TransferErrorKind.Unauthorized"/>)</exception>
    Task<string> GetTokenAsync(AccountInfo account, IAccountSecrets secrets, bool refresh, CancellationToken cancellationToken);

    /// <summary>
    /// Signs out of the account at the service, such as by revoking its token. Naqel deletes its secrets afterwards.
    /// </summary>
    Task SignOutAsync(AccountInfo account, IAccountSecrets secrets, CancellationToken cancellationToken);
}
