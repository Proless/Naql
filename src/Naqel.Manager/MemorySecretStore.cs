namespace Naqel.Manager;

/// <summary>
/// Secrets in memory, for tests and for hosts that keep nothing; they're gone when the process ends.
/// </summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Scope, string Key), string> _secrets = [];

    /// <inheritdoc />
    public Task<string?> GetAsync(string scope, string key, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_secrets.GetValueOrDefault((scope, key)));
        }
    }

    /// <inheritdoc />
    public Task SetAsync(string scope, string key, string? value, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (value == null)
            {
                _secrets.Remove((scope, key));
            }
            else
            {
                _secrets[(scope, key)] = value;
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteScopeAsync(string scope, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            foreach (var key in _secrets.Keys.Where(key => key.Scope == scope).ToList())
            {
                _secrets.Remove(key);
            }
        }
        return Task.CompletedTask;
    }
}
