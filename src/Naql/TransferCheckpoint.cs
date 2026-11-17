using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Naql;

/// <summary>
/// What an unfinished transfer had already written, so a later run can continue it, even in another process.
/// </summary>
/// <remarks>
/// Get one from <see cref="TransferOperation.GetCheckpoint"/>, save it with <see cref="Serialize"/>, and continue with
/// <see cref="TransferBuilder.ResumeFrom"/>. The checkpoint only counts bytes the target is known to have kept, so it
/// may be a little behind; resuming re-sends those bytes. If the content changed since, or the target lost its partial
/// data, the transfer starts over instead.
/// </remarks>
public sealed class TransferCheckpoint
{
    private const string Header = "naql-checkpoint";
    private const int FormatVersion = 1;
    private const string Null = "-";

    /// <summary>
    /// Creates a checkpoint.
    /// </summary>
    public TransferCheckpoint(long? length, string? sourceVersion, string? resumeToken, IEnumerable<CheckpointPart> parts)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The length can't be negative");
        }

        Length = length;
        SourceVersion = sourceVersion;
        ResumeToken = resumeToken;
        Parts = (parts ?? throw new ArgumentNullException(nameof(parts))).ToArray();
    }

    /// <summary>
    /// The length of the content, or <c>null</c> when it was unknown.
    /// </summary>
    public long? Length { get; }

    /// <summary>
    /// The <see cref="SourceInfo.Version"/> of the content. Resuming only continues when the source still reports it.
    /// </summary>
    public string? SourceVersion { get; }

    /// <summary>
    /// The target's <see cref="ITargetSession.ResumeToken"/>, which finds the partial data again.
    /// </summary>
    public string? ResumeToken { get; }

    /// <summary>
    /// The parts of the transfer and how much of each was kept.
    /// </summary>
    public IReadOnlyList<CheckpointPart> Parts { get; }

    /// <summary>
    /// The bytes kept in all parts together.
    /// </summary>
    public long Completed => Parts.Sum(part => part.Completed);

    /// <summary>
    /// Writes the checkpoint as a few lines of text.
    /// </summary>
    public string Serialize()
    {
        var text = new StringBuilder();
        text.Append(Header).Append(' ').Append(FormatVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("length ").Append(Length is long length ? Number(length) : Null).Append('\n');
        text.Append("version ").Append(Escape(SourceVersion)).Append('\n');
        text.Append("token ").Append(Escape(ResumeToken)).Append('\n');
        foreach (var part in Parts)
        {
            text.Append("part ")
                .Append(Number(part.Range.Start)).Append(' ')
                .Append(part.Range.Length is long partLength ? Number(partLength) : Null).Append(' ')
                .Append(Number(part.Completed)).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>
    /// Reads a checkpoint written by <see cref="Serialize"/>.
    /// </summary>
    /// <exception cref="FormatException">The text isn't a checkpoint this version can read</exception>
    public static TransferCheckpoint Deserialize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList();
        if (lines.Count < 4 || lines[0] != $"{Header} {FormatVersion}")
        {
            throw new FormatException($"Not a version {FormatVersion} Naql checkpoint");
        }

        var length = ParseOptionalNumber(Value(lines[1], "length"));
        var version = Unescape(Value(lines[2], "version"));
        var token = Unescape(Value(lines[3], "token"));
        var parts = new List<CheckpointPart>();
        foreach (var line in lines.Skip(4))
        {
            var fields = Value(line, "part").Split(' ');
            if (fields.Length != 3)
            {
                throw new FormatException($"A part needs a start, a length and the completed bytes: '{line}'");
            }

            try
            {
                parts.Add(new CheckpointPart(new ByteRange(ParseNumber(fields[0]), ParseOptionalNumber(fields[1])), ParseNumber(fields[2])));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new FormatException($"Invalid part '{line}': {ex.Message}", ex);
            }
        }

        try
        {
            return new TransferCheckpoint(length, version, token, parts);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Escape(string? value) => value == null ? Null : "=" + Uri.EscapeDataString(value);

    private static string? Unescape(string value)
    {
        if (value == Null)
        {
            return null;
        }
        if (!value.StartsWith('='))
        {
            throw new FormatException($"Invalid text value '{value}'");
        }
        return Uri.UnescapeDataString(value[1..]);
    }

    private static string Value(string line, string key)
    {
        var prefix = key + " ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new FormatException($"Expected '{key}' but found '{line}'");
        }
        return line[prefix.Length..];
    }

    private static long ParseNumber(string value)
    {
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new FormatException($"Invalid number '{value}'");
    }

    private static long? ParseOptionalNumber(string value) => value == Null ? null : ParseNumber(value);
}

/// <summary>
/// One part of a <see cref="TransferCheckpoint"/>.
/// </summary>
public sealed class CheckpointPart
{
    /// <summary>
    /// Creates a part.
    /// </summary>
    /// <param name="range">The bytes of the part</param>
    /// <param name="completed">How many bytes from the start of the part the target kept</param>
    public CheckpointPart(ByteRange range, long completed)
    {
        if (completed < 0 || completed > range.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(completed), completed, "The completed bytes must be between 0 and the length of the part");
        }

        Range = range;
        Completed = completed;
    }

    /// <summary>
    /// The bytes of the part.
    /// </summary>
    public ByteRange Range { get; }

    /// <summary>
    /// How many bytes from the start of the part the target kept.
    /// </summary>
    public long Completed { get; }

    /// <summary>
    /// Whether the whole part was kept.
    /// </summary>
    public bool IsComplete => Range.Length == Completed;
}
