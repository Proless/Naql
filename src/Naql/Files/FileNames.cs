using System;
using System.Buffers;
using System.Text;

namespace Naql.Files;

/// <summary>
/// Makes names from untrusted sources safe to use as a file name.
/// </summary>
/// <remarks>
/// The rules are the same on every OS, the strictest of them, so a name works wherever the file is copied to later
/// and a transfer names its file the same way everywhere.
/// </remarks>
internal static class FileNames
{
    /// <summary>
    /// The most UTF-8 bytes in a safe name. File systems allow 255; this leaves room for
    /// <see cref="FileTarget.PartialExtension"/> and the <c>" (n)"</c> that <see cref="FileConflict.KeepBoth"/> adds.
    /// </summary>
    public const int MaxBytes = 255 - 10 - 8;

    // Not allowed on Windows; '/' and '\' are treated as separators before this applies
    private static readonly SearchValues<char> Invalid = SearchValues.Create("\"<>|:*?\\/" + ControlCharacters());

    // Device names Windows reserves, with or without an extension
    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
    ];

    /// <summary>
    /// Keeps only the last part of a path, replaces characters that aren't allowed in file names, avoids names Windows
    /// reserves, and shortens long names while keeping their extension. Returns <c>null</c> when nothing usable is left.
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

        // Windows drops trailing dots and spaces, so "file." and "file" would be the same file there
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length == 0)
        {
            return null;
        }

        if (IsReserved(name))
        {
            name = "_" + name;
        }

        return Shorten(name);
    }

    private static bool IsReserved(string name)
    {
        var dot = name.IndexOf('.');
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return Array.Exists(Reserved, reserved => stem.Equals(reserved, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Cuts the name to <see cref="MaxBytes"/>, at whole characters, keeping a short extension.
    /// </summary>
    private static string Shorten(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) <= MaxBytes)
        {
            return name;
        }

        var dot = name.LastIndexOf('.');
        var extension = dot > 0 && name.Length - dot <= 16 ? name[dot..] : "";
        var budget = MaxBytes - Encoding.UTF8.GetByteCount(extension);

        var stem = new StringBuilder();
        var used = 0;
        foreach (var rune in name.AsSpan(0, name.Length - extension.Length).EnumerateRunes())
        {
            used += rune.Utf8SequenceLength;
            if (used > budget)
            {
                break;
            }
            stem.Append(rune.ToString());
        }

        return stem.ToString().TrimEnd('.', ' ') + extension;
    }

    private static string ControlCharacters()
    {
        var chars = new char[32];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)i;
        }
        return new string(chars);
    }
}
