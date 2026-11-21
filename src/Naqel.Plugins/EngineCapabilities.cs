namespace Naqel.Plugins;

/// <summary>
/// What an engine can do with its jobs, so the manager and the UI offer only that.
/// </summary>
public sealed record EngineCapabilities
{
    /// <summary>Jobs can be paused and resumed, continuing where they stopped.</summary>
    public bool Pause { get; init; }

    /// <summary>Jobs can change their own limits, also while they run.</summary>
    public bool JobLimits { get; init; }

    /// <summary>All jobs together can be limited, also while they run.</summary>
    public bool EngineLimits { get; init; }

    /// <summary>Removing a job can also delete what it transferred.</summary>
    public bool DeleteData { get; init; }

    /// <summary>Jobs can keep uploading after their content is complete, such as torrents that seed.</summary>
    public bool Seeding { get; init; }
}
