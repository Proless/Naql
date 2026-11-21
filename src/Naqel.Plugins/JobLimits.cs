namespace Naqel.Plugins;

/// <summary>
/// Limits for one job, which can change while it runs.
/// </summary>
public sealed record JobLimits
{
    /// <summary>
    /// No limits of the job's own: the engine's limits and defaults apply.
    /// </summary>
    public static JobLimits None { get; } = new();

    /// <summary>The highest average rate in bytes per second, or 0 for no limit of the job's own.</summary>
    public long MaxBytesPerSecond
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(MaxBytesPerSecond));
            field = value;
        }
    }

    /// <summary>How many connections the job may use at once, or <c>null</c> for the engine's default.</summary>
    public int? MaxConnections
    {
        get;
        init
        {
            if (value is int connections)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(connections, nameof(MaxConnections));
            }
            field = value;
        }
    }
}
