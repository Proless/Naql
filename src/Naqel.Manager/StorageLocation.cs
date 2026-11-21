using Naqel.Storage;

namespace Naqel.Manager;

/// <summary>
/// A named place to save to and read from, such as "media" on a network share or "backups" in a cloud drive: a
/// storage set up once and used by every job that names it.
/// </summary>
/// <param name="Name">The location's name, which is the host of its references, such as <c>location://media/</c></param>
/// <param name="Storage">Where its files live</param>
/// <param name="Mode">What a write does when a file is there already</param>
public sealed record StorageLocation(string Name, IStorage Storage, WriteMode Mode = WriteMode.Overwrite)
{
    /// <summary>
    /// The reference for the location's root, such as <c>location://media/</c>.
    /// </summary>
    public Uri Uri => StorageLocations.UriOf(Name);
}
