namespace Naqel.Manager;

/// <summary>
/// Settings in memory, for tests and for hosts that keep nothing.
/// </summary>
public sealed class MemorySettingsStore : ISettingsStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Dictionary<string, string>> _scopes = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(string scope, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyDictionary<string, string> values = _scopes.TryGetValue(scope, out var found) ? new Dictionary<string, string>(found) : new Dictionary<string, string>();
            return Task.FromResult(values);
        }
    }

    /// <inheritdoc />
    public Task SetAsync(string scope, string key, string? value, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_scopes.TryGetValue(scope, out var values))
            {
                values = new Dictionary<string, string>(StringComparer.Ordinal);
                _scopes[scope] = values;
            }
            if (value == null)
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value;
            }
        }
        return Task.CompletedTask;
    }
}
