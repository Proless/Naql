namespace Naqel.Plugins;

/// <summary>
/// Limits for all jobs of one engine together, which can change while they run.
/// </summary>
public sealed record EngineLimits
{
    /// <summary>
    /// No limits.
    /// </summary>
    public static EngineLimits None { get; } = new();

    /// <summary>The highest combined rate of the engine's jobs in bytes per second, or 0 for no limit.</summary>
    public long MaxBytesPerSecond
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(MaxBytesPerSecond));
            field = value;
        }
    }
}
