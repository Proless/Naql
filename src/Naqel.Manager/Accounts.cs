using System.Text.Json;
using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// The user's accounts: signed in to through the account providers plugins bring, with their secrets in the secret
/// store, and tokens for the plugins that use them.
/// </summary>
public sealed class Accounts : IAccounts
{
    private const string Scope = "naqel.accounts";
    private const string ListKey = "list";

    private readonly ISecretStore _store;
    private readonly Interactions _interactions;
    private readonly Lock _lock = new();
    private readonly List<IAccountProvider> _providers = [];
    private List<AccountInfo> _accounts = [];

    internal Accounts(ISecretStore store, Interactions interactions)
    {
        _store = store;
        _interactions = interactions;
    }

    /// <summary>
    /// The services accounts can be signed in to.
    /// </summary>
    public IReadOnlyList<IAccountProvider> Providers
    {
        get
        {
            lock (_lock)
            {
                return [.. _providers];
            }
        }
    }

    /// <summary>
    /// Every account, in the order they were signed in to.
    /// </summary>
    public IReadOnlyList<AccountInfo> All
    {
        get
        {
            lock (_lock)
            {
                return [.. _accounts];
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AccountInfo> OfService(string service) => [.. All.Where(account => account.Service == service)];

    /// <summary>
    /// Signs in to a new account of <paramref name="service"/>, asking the user what its provider needs. Signing in to
    /// an account again replaces it.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No provider signs in to <paramref name="service"/></exception>
    public async Task<AccountInfo> SignInAsync(string service, CancellationToken cancellationToken = default)
    {
        var provider = ProviderOf(service);
        var account = await provider.SignInAsync(_interactions.General, id => SecretsOf(service, id), cancellationToken).ConfigureAwait(false);
        if (account.Service != service)
        {
            throw new InvalidOperationException($"The provider for {service} signed in to an account of {account.Service}");
        }
        lock (_lock)
        {
            _accounts = [.. _accounts.Where(known => !Same(known, account)), account];
        }
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return account;
    }

    /// <summary>
    /// Signs out of an account and forgets it with its secrets.
    /// </summary>
    /// <exception cref="KeyNotFoundException">There's no such account</exception>
    public async Task SignOutAsync(string service, string accountId, CancellationToken cancellationToken = default)
    {
        var account = Find(service, accountId);
        await ProviderOf(service).SignOutAsync(account, SecretsOf(service, accountId), cancellationToken).ConfigureAwait(false);
        await _store.DeleteScopeAsync(ScopeOf(service, accountId), cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _accounts = [.. _accounts.Where(known => !Same(known, account))];
        }
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<string> GetTokenAsync(string service, string accountId, bool refresh, CancellationToken cancellationToken)
    {
        var account = Find(service, accountId);
        return ProviderOf(service).GetTokenAsync(account, SecretsOf(service, accountId), refresh, cancellationToken);
    }

    internal void AddProvider(IAccountProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_lock)
        {
            if (_providers.Any(known => known.Service == provider.Service))
            {
                throw new InvalidOperationException($"There's a provider for {provider.Service} already");
            }
            _providers.Add(provider);
        }
    }

    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        var text = await _store.GetAsync(Scope, ListKey, cancellationToken).ConfigureAwait(false);
        var accounts = text == null ? [] : JsonSerializer.Deserialize<List<AccountInfo>>(text) ?? [];
        lock (_lock)
        {
            _accounts = accounts;
        }
    }

    private Task SaveAsync(CancellationToken cancellationToken) =>
        _store.SetAsync(Scope, ListKey, JsonSerializer.Serialize(All), cancellationToken);

    private IAccountProvider ProviderOf(string service) =>
        Providers.FirstOrDefault(provider => provider.Service == service) ?? throw new KeyNotFoundException($"No provider signs in to {service}");

    private AccountInfo Find(string service, string accountId) =>
        All.FirstOrDefault(account => account.Service == service && account.Id == accountId)
            ?? throw new KeyNotFoundException($"There's no {service} account {accountId}");

    private Secrets SecretsOf(string service, string accountId) => new Secrets(_store, ScopeOf(service, accountId));

    private static string ScopeOf(string service, string accountId) => $"naqel.account:{service}:{accountId}";

    private static bool Same(AccountInfo a, AccountInfo b) => a.Service == b.Service && a.Id == b.Id;

    private sealed class Secrets(ISecretStore store, string scope) : IAccountSecrets
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => store.GetAsync(scope, key, cancellationToken);

        public Task SetAsync(string key, string? value, CancellationToken cancellationToken) => store.SetAsync(scope, key, value, cancellationToken);
    }
}
