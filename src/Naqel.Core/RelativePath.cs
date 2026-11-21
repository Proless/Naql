using System;
using System.Collections.Generic;

namespace Naqel;

/// <summary>
/// The relative paths that place resolved content inside a structure, such as <c>album/02.mp3</c>.
/// </summary>
/// <remarks>
/// A relative path is empty for a single item, or segments separated by <c>/</c>. It has no leading or trailing
/// <c>/</c>, and no segment is empty, <c>.</c> or <c>..</c>, so it can't leave the place it's relative to. Any other
/// character is allowed; a target resolver makes each segment acceptable to its own storage.
/// </remarks>
public static class RelativePath
{
    /// <summary>
    /// Whether <paramref name="path"/> is a valid relative path.
    /// </summary>
    public static bool IsValid(string? path) => path != null && Problem(path) == null;

    /// <summary>
    /// The segments of <paramref name="path"/>, none for an empty path.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> isn't a valid relative path</exception>
    public static IReadOnlyList<string> Split(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Problem(path) is { } problem)
        {
            throw new ArgumentException($"'{path}' isn't a relative path: {problem}", nameof(path));
        }
        return path.Length == 0 ? Array.Empty<string>() : path.Split('/');
    }

    /// <summary>
    /// <paramref name="child"/> placed inside <paramref name="parent"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Either isn't a valid relative path</exception>
    public static string Combine(string parent, string child)
    {
        Split(parent);
        Split(child);
        return parent.Length == 0 ? child : child.Length == 0 ? parent : parent + "/" + child;
    }

    private static string? Problem(string path)
    {
        if (path.Length == 0)
        {
            return null;
        }
        if (path[0] == '/' || path[path.Length - 1] == '/')
        {
            return "it starts or ends with '/'";
        }
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return $"it has a segment '{segment}'";
            }
        }
        return null;
    }
}
