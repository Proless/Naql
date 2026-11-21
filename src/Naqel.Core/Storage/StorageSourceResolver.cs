using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Naqel.Storage;

/// <summary>
/// Resolves references under a root URI into sources from a storage: a file into one source, a folder into a source
/// for each of its files, with its path inside the folder, so a whole folder copies to any target. Any storage gets its
/// resolving from this class.
/// </summary>
/// <remarks>
/// In a storage with <see cref="StorageCapabilities.DuplicateNames"/>, two files of a folder can have the same path;
/// the later ones get a number, as in <c>a (1).txt</c>, the way <see cref="WriteMode.KeepBoth"/> names a file, so each
/// keeps a place of its own wherever the folder goes.
/// </remarks>
public sealed class StorageSourceResolver : ISourceResolver, IStorageLocator
{
    private readonly StorageRoot _root;

    /// <summary>
    /// A resolver for the references under <paramref name="root"/>, such as <c>dropbox:/</c>, in
    /// <paramref name="storage"/>; a folder includes the files of its subfolders unless <paramref name="recursive"/> is
    /// <c>false</c>.
    /// </summary>
    public StorageSourceResolver(Uri root, IStorage storage, bool recursive = true)
    {
        _root = new StorageRoot(root, storage);
        Recursive = recursive;
    }

    /// <summary>
    /// The root URI the storage's paths are under.
    /// </summary>
    public Uri Root => _root.Root;

    /// <summary>
    /// The storage.
    /// </summary>
    public IStorage Storage => _root.Storage;

    /// <summary>
    /// Whether a folder includes the files of its subfolders.
    /// </summary>
    public bool Recursive { get; }

    /// <inheritdoc />
    public bool CanResolve(Uri reference) => _root.Contains(reference);

    /// <inheritdoc />
    public bool TryLocate(Uri reference, [NotNullWhen(true)] out IStorage? storage, [NotNullWhen(true)] out string? path)
    {
        if (!_root.Contains(reference))
        {
            (storage, path) = (null, null);
            return false;
        }
        (storage, path) = (Storage, _root.PathOf(reference));
        return true;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Resolved> ResolveAsync(Uri reference, ResolveContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var path = _root.PathOf(reference);
        var item = await Storage.GetAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw new TransferException($"There's nothing at {reference}", TransferErrorKind.NotFound, side: TransferSide.Source);
        if (!item.IsFolder)
        {
            yield return new ResolvedSource(Storage.OpenRead(item), "", Describe(item));
            yield break;
        }

        var comparer = Storage.Capabilities.IgnoresCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var taken = new HashSet<string>(comparer);
        await foreach (var child in Storage.ListAsync(item.Path, Recursive, cancellationToken).ConfigureAwait(false))
        {
            if (child.IsFolder)
            {
                continue;
            }
            var relative = item.Path.Length == 0 ? child.Path : child.Path.Substring(item.Path.Length).TrimStart('/');
            yield return new ResolvedSource(Storage.OpenRead(child), Unique(relative, taken), Describe(child));
        }
    }

    private static SourceInfo Describe(StorageItem file) => new()
    {
        Length = file.Size,
        Name = file.Name,
        LastModified = file.Modified,
        Version = file.Version
    };

    // The first file keeps its path; later ones with the same path get a number before the extension
    private static string Unique(string path, HashSet<string> taken)
    {
        if (taken.Add(path))
        {
            return path;
        }
        var slash = path.LastIndexOf('/');
        var folder = path.Remove(slash + 1);
        var name = path.Substring(slash + 1);
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name.Remove(dot), name.Substring(dot)) : (name, "");
        for (var n = 1; ; n++)
        {
            var candidate = FormattableString.Invariant($"{folder}{stem} ({n}){extension}");
            if (taken.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
