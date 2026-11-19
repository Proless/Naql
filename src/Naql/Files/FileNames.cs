using System;
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
    private static readonly char[] Invalid = ("\"<>|:*?\\/" + ControlCharacters()).ToCharArray();

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
        if (name.IndexOfAny(Invalid) >= 0)
        {
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(Invalid, chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }
            name = new string(chars);
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

        // Counts whole characters: a surrogate pair is one character of 4 bytes, and is never split
        var stemLength = name.Length - extension.Length;
        var end = 0;
        var used = 0;
        while (end < stemLength)
        {
            var c = name[end];
            var pair = char.IsHighSurrogate(c) && end + 1 < stemLength && char.IsLowSurrogate(name[end + 1]);
            used += pair ? 4 : c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
            if (used > budget)
            {
                break;
            }
            end += pair ? 2 : 1;
        }

        return name.Substring(0, end).TrimEnd('.', ' ') + extension;
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
