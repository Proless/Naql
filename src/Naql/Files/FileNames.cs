using System;
using System.Buffers;
using System.IO;

namespace Naql.Files;

/// <summary>
/// Makes names from untrusted sources safe to use as a file name.
/// </summary>
internal static class FileNames
{
    private static readonly SearchValues<char> Invalid = SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>
    /// Keeps only the last part of a path, replaces characters that aren't allowed in file names, and returns
    /// <c>null</c> when nothing usable is left.
    /// </summary>
    public static string? ToSafeName(string? name)
    {
        if (name == null)
        {
            return null;
        }

        // Treat both separators as directories on every OS, so a name can never leave the target directory
        name = name.Trim().Trim('"').Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.AsSpan().ContainsAny(Invalid))
        {
            name = string.Create(name.Length, name, static (chars, source) =>
            {
                for (var i = 0; i < source.Length; i++)
                {
                    chars[i] = Invalid.Contains(source[i]) ? '_' : source[i];
                }
            });
        }

        name = name.Trim();
        return name is "" or "." or ".." ? null : name;
    }
}
