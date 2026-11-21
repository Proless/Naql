using System;
using System.Collections.Generic;

namespace Naqel.Storage;

/// <summary>
/// A file or folder in a storage.
/// </summary>
/// <param name="Path">Where it is, relative to the storage's root, in the case the storage keeps it</param>
/// <param name="Name">Its name, the last segment of its path</param>
/// <param name="IsFolder">Whether it's a folder; otherwise it's a file</param>
public sealed record StorageItem(string Path, string Name, bool IsFolder)
{
    private static readonly IReadOnlyDictionary<string, string> NoHashes = new Dictionary<string, string>();

    /// <summary>
    /// The storage's own id for it, for storages with <see cref="StorageCapabilities.Ids"/>, which stays the same when
    /// it moves. It tells apart items with the same path in storages with <see cref="StorageCapabilities.DuplicateNames"/>.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>A file's length in bytes, when known.</summary>
    public long? Size { get; init; }

    /// <summary>When a file's content last changed, when known.</summary>
    public DateTimeOffset? Modified { get; init; }

    /// <summary>
    /// What changes whenever a file's content does, such as an ETag or a revision, when the storage has one.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Hashes of a file's content the storage keeps, by the storage's name for each, such as <c>md5</c> or
    /// <c>content_hash</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Hashes { get; init; } = NoHashes;
}
