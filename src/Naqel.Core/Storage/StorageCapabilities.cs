namespace Naqel.Storage;

/// <summary>
/// What a storage's paths and items are like.
/// </summary>
public sealed record StorageCapabilities
{
    /// <summary>
    /// Paths match whatever their case, as on Windows, macOS and most cloud drives.
    /// </summary>
    public bool IgnoresCase { get; init; }

    /// <summary>
    /// Items have ids of their own (<see cref="StorageItem.Id"/>), which stay the same when they move.
    /// </summary>
    public bool Ids { get; init; }

    /// <summary>
    /// Two items in one folder can have the same name, as in Google Drive, so a path doesn't always name one item; their
    /// ids tell them apart.
    /// </summary>
    public bool DuplicateNames { get; init; }
}
