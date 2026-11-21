using System.Globalization;
using System.Text.Json;

namespace Naql.Samples.Dropbox;

/// <summary>
/// A file or folder in Dropbox, as its metadata describes it.
/// </summary>
/// <param name="IsFile">Whether it's a file; otherwise it's a folder</param>
/// <param name="Name">Its name, with its case</param>
/// <param name="Path">Its path, with its case, such as <c>/Photos/a.jpg</c>; empty for the root</param>
/// <param name="Id">Its id, such as <c>id:a4ayc_80_OEAAAAAAAAAXw</c>, which stays the same when it moves</param>
/// <param name="Size">A file's length in bytes</param>
/// <param name="Rev">A file's revision, which changes with its content</param>
/// <param name="Modified">When a file's content last changed on Dropbox</param>
public sealed record DropboxEntry(bool IsFile, string Name, string Path, string? Id, long? Size, string? Rev, DateTimeOffset? Modified)
{
    /// <summary>
    /// The root folder.
    /// </summary>
    public static DropboxEntry Root { get; } = new(false, "", "", null, null, null, null);

    /// <summary>
    /// Reads metadata as the API returns it, a file's or a folder's.
    /// </summary>
    internal static DropboxEntry Parse(JsonElement json)
    {
        string? Text(string name) => json.TryGetProperty(name, out var value) ? value.GetString() : null;

        var isFile = Text(".tag") == "file";
        return new DropboxEntry(
            isFile,
            Text("name") ?? "",
            Text("path_display") ?? "",
            Text("id"),
            isFile && json.TryGetProperty("size", out var size) ? size.GetInt64() : null,
            Text("rev"),
            Text("server_modified") is { } modified ? DateTimeOffset.Parse(modified, CultureInfo.InvariantCulture) : null);
    }

    /// <summary>
    /// What the entry says about its content, before downloading it.
    /// </summary>
    internal SourceInfo ToSourceInfo() => new()
    {
        Length = Size,
        Name = Name,
        Identity = Identity,
        LastModified = Modified
    };

    /// <summary>
    /// What the content is known by for checkpoints, whatever link it's downloaded from: its id, which survives a
    /// rename, and its path for an entry without one.
    /// </summary>
    internal string Identity => DropboxPath.Scheme + ":" + (Id ?? Path.ToLowerInvariant());
}
