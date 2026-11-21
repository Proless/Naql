using System;
using System.Linq;

namespace Naqel.Storage;

/// <summary>
/// Maps references under a root URI, such as <c>dropbox:/</c> or <c>location://media/</c>, to paths in a storage.
/// </summary>
internal sealed class StorageRoot
{
    private readonly string _rootPath;
    private readonly StringComparison _comparison;

    public StorageRoot(Uri root, IStorage storage)
    {
        if (root is not { IsAbsoluteUri: true })
        {
            throw new ArgumentException("A storage's root must be an absolute URI", nameof(root));
        }
        Root = root;
        Storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _rootPath = root.AbsolutePath.EndsWith('/') ? root.AbsolutePath : root.AbsolutePath + "/";
        _comparison = storage.Capabilities.IgnoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public Uri Root { get; }

    public IStorage Storage { get; }

    public bool Contains(Uri reference) =>
        reference is { IsAbsoluteUri: true } &&
        reference.Scheme == Root.Scheme &&
        string.Equals(reference.Host, Root.Host, StringComparison.OrdinalIgnoreCase) &&
        reference.Port == Root.Port &&
        (reference.AbsolutePath + "/").StartsWith(_rootPath, _comparison);

    /// <summary>
    /// The storage path of <paramref name="reference"/>, which <see cref="Contains"/> said is under the root.
    /// </summary>
    /// <exception cref="ArgumentException">The path leaves the root, such as with <c>..</c></exception>
    public string PathOf(Uri reference)
    {
        var rest = reference.AbsolutePath.Length > _rootPath.Length ? reference.AbsolutePath.Substring(_rootPath.Length) : "";
        // AbsolutePath is escaped: split first, then unescape each segment
        var path = string.Join("/", rest.Split('/').Where(segment => segment.Length > 0).Select(Uri.UnescapeDataString));
        RelativePath.Split(path);
        return path;
    }
}
