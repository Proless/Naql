namespace Naqel.Samples.Dropbox;

/// <summary>
/// Converts between <c>dropbox:</c> references and Dropbox paths. A reference is <c>dropbox:/Folder/file.txt</c>, or
/// <c>dropbox:/</c> for the root, which the API names with an empty path.
/// </summary>
public static class DropboxPath
{
    /// <summary>
    /// The scheme of Dropbox references.
    /// </summary>
    public const string Scheme = "dropbox";

    /// <summary>
    /// Whether <paramref name="reference"/> is a <c>dropbox:</c> reference.
    /// </summary>
    public static bool IsDropbox(Uri reference) => reference is { IsAbsoluteUri: true } && reference.Scheme == Scheme;

    /// <summary>
    /// The Dropbox path of <paramref name="reference"/>: <c>/Folder/file.txt</c>, or empty for the root.
    /// </summary>
    /// <exception cref="ArgumentException">Not a <c>dropbox:</c> reference with a path from the root</exception>
    public static string FromReference(Uri reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!IsDropbox(reference) || !reference.AbsolutePath.StartsWith('/'))
        {
            throw new ArgumentException($"'{reference}' isn't a dropbox:/ reference", nameof(reference));
        }

        // AbsolutePath is escaped and keeps its case: split first, then unescape each segment, so an escaped slash
        // stays part of a name
        var segments = reference.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString);
        var path = string.Join('/', segments);
        return path.Length == 0 ? "" : "/" + path;
    }

    /// <summary>
    /// The <c>dropbox:</c> reference for a Dropbox path.
    /// </summary>
    public static Uri ToReference(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        return new Uri($"{Scheme}:/{string.Join('/', segments)}");
    }

    /// <summary>
    /// <paramref name="relativePath"/>, a <see cref="RelativePath"/>, inside the folder <paramref name="folder"/>.
    /// </summary>
    public static string Combine(string folder, string relativePath)
    {
        var segments = RelativePath.Split(relativePath);
        return segments.Count == 0 ? folder : folder.TrimEnd('/') + "/" + string.Join('/', segments);
    }
}
