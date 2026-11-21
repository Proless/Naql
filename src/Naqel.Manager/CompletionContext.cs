using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// What one completion step gets, collecting the data it sets until the manager keeps it.
/// </summary>
internal sealed class CompletionContext(JobInfo job) : ICompletionContext
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string?> _changes = [];

    public JobInfo Job { get; } = job;

    public void SetData(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_lock)
        {
            _changes[key] = value;
        }
    }

    public IReadOnlyDictionary<string, string?> TakeChanges()
    {
        lock (_lock)
        {
            return new Dictionary<string, string?>(_changes);
        }
    }
}
