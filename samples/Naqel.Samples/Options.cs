using System.Globalization;

namespace Naqel.Samples;

/// <summary>
/// A minimal command line parser: positional arguments plus <c>--name value</c> options.
/// </summary>
public sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positionals { get; } = new();

    public static Options Parse(string[] args)
    {
        var options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                options._values[args[i][2..]] = hasValue ? args[++i] : "true";
            }
            else
            {
                options.Positionals.Add(args[i]);
            }
        }

        return options;
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public double GetNumber(string name, double defaultValue)
    {
        var value = Get(name);
        if (value == null)
        {
            return defaultValue;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0
            ? number
            : throw new ArgumentException($"--{name} must be a positive number, not '{value}'");
    }
}
