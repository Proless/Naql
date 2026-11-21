using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Naqel.Storage;

namespace Naqel.Manager;

/// <summary>
/// The storage locations jobs can save to and read from, named by references such as
/// <c>location://media/movies/</c>. It resolves sources and targets in them, so Naqel's own engine, and anything else
/// that takes resolvers, can use every location without knowing what storage it is.
/// </summary>
/// <remarks>
/// A location's name is the host of its references, and hosts ignore case, so names are lowercase letters, digits and
/// hyphens. Locations can be added and removed while jobs run. Every <c>location:</c> reference is this resolver's, so a
/// job whose location is gone fails with <see cref="TransferErrorKind.NotFound"/>, naming the location, when it next
/// resolves it.
/// </remarks>
public sealed partial class StorageLocations : ISourceResolver, ITargetResolver, IStorageLocator
{
    /// <summary>
    /// The scheme of location references.
    /// </summary>
    public const string Scheme = "location";

    private readonly Lock _lock = new();
    private volatile Entry[] _entries = [];

    /// <summary>
    /// Every location, in the order they were added.
    /// </summary>
    public IReadOnlyList<StorageLocation> All => [.. _entries.Select(entry => entry.Location)];

    /// <summary>
    /// The reference for <paramref name="path"/> in the location <paramref name="name"/>, such as
    /// <c>location://media/My%20Trip/</c>.
    /// </summary>
    /// <param name="name">The location's name</param>
    /// <param name="path">A path in it, in the form <see cref="RelativePath"/> describes; empty for its root</param>
    /// <param name="folder">Whether the path is a folder, so the reference ends with <c>/</c></param>
    public static Uri UriOf(string name, string path = "", bool folder = true)
    {
        ArgumentNullException.ThrowIfNull(name);
        var segments = RelativePath.Split(path).Select(Uri.EscapeDataString);
        var text = $"{Scheme}://{name}/{string.Join('/', segments)}";
        return new Uri(folder && path.Length > 0 ? text + "/" : text);
    }

    /// <summary>
    /// Adds a location.
    /// </summary>
    /// <exception cref="ArgumentException">The name isn't lowercase letters, digits and hyphens, or a location has it already</exception>
    public StorageLocation Add(string name, IStorage storage, WriteMode mode = WriteMode.Overwrite)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(storage);
        if (!ValidName().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' isn't a location name: use lowercase letters, digits and hyphens, starting with a letter or digit", nameof(name));
        }

        var location = new StorageLocation(name, storage, mode);
        var entry = new Entry(location, new StorageSourceResolver(location.Uri, storage), new StorageTargetResolver(location.Uri, storage, mode));
        lock (_lock)
        {
            if (_entries.Any(existing => existing.Location.Name == name))
            {
                throw new ArgumentException($"There's a location '{name}' already", nameof(name));
            }
            _entries = [.. _entries, entry];
        }
        return location;
    }

    /// <summary>
    /// Removes a location; its files stay where they are.
    /// </summary>
    /// <returns>Whether there was such a location</returns>
    public bool Remove(string name)
    {
        lock (_lock)
        {
            var kept = _entries.Where(entry => entry.Location.Name != name).ToArray();
            var removed = kept.Length != _entries.Length;
            _entries = kept;
            return removed;
        }
    }

    /// <summary>
    /// The location named <paramref name="name"/>, or <c>null</c>.
    /// </summary>
    public StorageLocation? Find(string name) => Array.Find(_entries, entry => entry.Location.Name == name)?.Location;

    /// <summary>
    /// Whether <paramref name="reference"/> is a location reference, such as <c>location://media/a.txt</c>, whether or
    /// not that location exists.
    /// </summary>
    public bool CanResolve(Uri reference) => reference is { IsAbsoluteUri: true } && reference.Scheme == Scheme;

    /// <inheritdoc />
    public async IAsyncEnumerable<Resolved> ResolveAsync(Uri reference, ResolveContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var entry = EntryOf(reference) ?? throw NoSuchLocation(reference, TransferSide.Source);
        await foreach (var resolved in entry.Sources.ResolveAsync(reference, context, cancellationToken).ConfigureAwait(false))
        {
            yield return resolved;
        }
    }

    /// <inheritdoc />
    public ValueTask<ITransferTarget> ResolveAsync(Uri destination, string relativePath, SourceInfo? info, ResolveContext context, CancellationToken cancellationToken)
    {
        var entry = EntryOf(destination) ?? throw NoSuchLocation(destination, TransferSide.Target);
        return entry.Targets.ResolveAsync(destination, relativePath, info, context, cancellationToken);
    }

    /// <inheritdoc />
    public bool TryLocate(Uri reference, [NotNullWhen(true)] out IStorage? storage, [NotNullWhen(true)] out string? path)
    {
        if (EntryOf(reference) is { } entry)
        {
            return entry.Sources.TryLocate(reference, out storage, out path);
        }
        (storage, path) = (null, null);
        return false;
    }

    private Entry? EntryOf(Uri? reference) =>
        reference is { IsAbsoluteUri: true } && reference.Scheme == Scheme
            ? Array.Find(_entries, entry => entry.Location.Name == reference.Host)
            : null;

    private static TransferException NoSuchLocation(Uri reference, TransferSide side) =>
        new($"There's no storage location '{reference.Host}'", TransferErrorKind.NotFound, side: side);

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex ValidName();

    private sealed record Entry(StorageLocation Location, StorageSourceResolver Sources, StorageTargetResolver Targets);
}
