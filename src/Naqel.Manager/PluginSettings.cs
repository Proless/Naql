using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// One plugin's settings: the values the user set, kept in the settings store, over its definitions' defaults.
/// </summary>
internal sealed class PluginSettings(IReadOnlyDictionary<string, string> values) : IPluginSettings
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _values = new(values, StringComparer.Ordinal);
    private readonly List<SettingDefinition> _definitions = [];

    public event Action<string>? Changed;

    public IReadOnlyList<SettingDefinition> Definitions
    {
        get
        {
            lock (_lock)
            {
                return [.. _definitions];
            }
        }
    }

    public string? GetValue(string key)
    {
        lock (_lock)
        {
            return _values.TryGetValue(key, out var value) ? value : _definitions.Find(definition => definition.Key == key)?.Default;
        }
    }

    public void Define(IReadOnlyList<SettingDefinition> definitions)
    {
        lock (_lock)
        {
            foreach (var definition in definitions)
            {
                _definitions.RemoveAll(known => known.Key == definition.Key);
                _definitions.Add(definition);
            }
        }
    }

    public void Set(string key, string? value)
    {
        lock (_lock)
        {
            if (value == null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }
        }
        Changed?.Invoke(key);
    }
}
