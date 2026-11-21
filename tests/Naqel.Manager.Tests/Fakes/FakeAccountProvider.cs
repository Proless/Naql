using Naqel.Plugins;

namespace Naqel.Manager.Tests.Fakes;

/// <summary>
/// Signs in like an OAuth service: asks the user to open its sign-in page, takes the code the page hands back, and
/// keeps a refresh token that each refresh replaces.
/// </summary>
public sealed class FakeAccountProvider(string service = "cloud") : IAccountProvider
{
    private int _issued;

    public string Service { get; } = service;

    public string Title => "Cloud";

    public List<AccountInfo> SignedOut { get; } = [];

    public async Task<AccountInfo> SignInAsync(IInteraction interaction, Func<string, IAccountSecrets> secrets, CancellationToken cancellationToken)
    {
        var answer = await interaction.AskAsync(new InteractionRequest(InteractionKind.OpenUrl, "Sign in to Cloud") { Url = new Uri("https://cloud.test/authorize") }, cancellationToken);
        if (answer.Value is not { } code || !code.StartsWith("code-", StringComparison.Ordinal))
        {
            throw new TransferException("Signing in was canceled", TransferErrorKind.Unauthorized);
        }
        var account = new AccountInfo(Service, code["code-".Length..], $"{code["code-".Length..]}@cloud.test");
        await secrets(account.Id).SetAsync("refresh", "refresh-0", cancellationToken);
        return account;
    }

    public async Task<string> GetTokenAsync(AccountInfo account, IAccountSecrets secrets, bool refresh, CancellationToken cancellationToken)
    {
        var current = await secrets.GetAsync("refresh", cancellationToken)
            ?? throw new TransferException("Sign in again", TransferErrorKind.Unauthorized);
        if (refresh)
        {
            current = $"refresh-{Interlocked.Increment(ref _issued)}";
            await secrets.SetAsync("refresh", current, cancellationToken);
        }
        return $"token-for-{account.Id}-{current}";
    }

    public Task SignOutAsync(AccountInfo account, IAccountSecrets secrets, CancellationToken cancellationToken)
    {
        SignedOut.Add(account);
        return Task.CompletedTask;
    }
}
