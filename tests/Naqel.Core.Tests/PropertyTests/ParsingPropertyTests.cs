using CsCheck;
using Naqel.Engine;
using Naqel.Files;
using System.Text;

namespace Naqel.Core.Tests.PropertyTests;

/// <summary>
/// Inputs that come from outside, generated and mutated: checkpoint text, file names from servers, and the content
/// sizes and target limits the planner splits.
/// </summary>
public sealed class ParsingPropertyTests
{
    private static Action<string> Output => TestContext.Current.TestOutputHelper!.WriteLine;

    #region Checkpoints

    /// <summary>
    /// Valid checkpoints: parts that cover the content in order, with any amount kept in each.
    /// </summary>
    private static readonly Gen<TransferCheckpoint> CheckpointGen =
        from sizes in Gen.Int[0, 5_000].Array[1, 6]
        from knownLength in Gen.Bool
        from kept in Gen.Double[0, 1].Array[sizes.Length]
        from identity in Gen.String[0, 20].Null()
        from version in Gen.String[0, 20].Null()
        from token in Gen.String[0, 40].Null()
        select Checkpoint(sizes, knownLength, kept, identity, version, token);

    private static TransferCheckpoint Checkpoint(int[] sizes, bool knownLength, double[] kept, string? identity, string? version, string? token)
    {
        var parts = new List<CheckpointPart>();
        long start = 0;
        for (var i = 0; i < sizes.Length; i++)
        {
            // Without a known length, the last part may be open-ended
            var openEnded = !knownLength && i == sizes.Length - 1;
            var range = openEnded ? new ByteRange(start) : new ByteRange(start, sizes[i]);
            var completed = openEnded ? (long)(kept[i] * 10_000) : (long)(kept[i] * sizes[i]);
            parts.Add(new CheckpointPart(range, completed));
            start += sizes[i];
        }
        return new TransferCheckpoint(knownLength ? start : null, identity, version, token, parts);
    }

    [Fact]
    public void A_checkpoint_survives_serialization_unchanged()
    {
        CheckpointGen.Sample(checkpoint =>
        {
            var copy = TransferCheckpoint.Deserialize(checkpoint.Serialize());

            Assert.Equal(checkpoint.Length, copy.Length);
            Assert.Equal(checkpoint.SourceIdentity, copy.SourceIdentity);
            Assert.Equal(checkpoint.SourceVersion, copy.SourceVersion);
            Assert.Equal(checkpoint.ResumeToken, copy.ResumeToken);
            Assert.Equal(checkpoint.Parts.Select(part => (part.Range, part.Completed)), copy.Parts.Select(part => (part.Range, part.Completed)));
        }, writeLine: Output);
    }

    private enum Mutation
    {
        ReplaceChar,
        InsertChar,
        DeleteChar,
        DropLine,
        RepeatLine,
        SwapLines
    }

    /// <summary>
    /// A damaged or edited checkpoint either reads as a valid checkpoint or is rejected as a format error; it never
    /// throws anything else, and whatever it reads still covers the content exactly once.
    /// </summary>
    [Fact]
    public void Damaged_checkpoint_text_is_rejected_as_a_format_error_or_read_as_a_valid_checkpoint()
    {
        var mutationGen = Gen.Select(Gen.Enum<Mutation>(), Gen.Int[0, 10_000], Gen.Char["0123456789-=% \nabcpartlengthversiontokenidentity"]);

        Gen.Select(CheckpointGen, mutationGen.Array[1, 5]).Sample((checkpoint, mutations) =>
        {
            var text = mutations.Aggregate(checkpoint.Serialize(), (current, mutation) => Mutate(current, mutation.Item1, mutation.Item2, mutation.Item3));

            TransferCheckpoint read;
            try
            {
                read = TransferCheckpoint.Deserialize(text);
            }
            catch (FormatException)
            {
                return;
            }

            long next = 0;
            foreach (var part in read.Parts)
            {
                Assert.Equal(next, part.Range.Start);
                Assert.InRange(part.Completed, 0, part.Range.Length ?? long.MaxValue);
                next += part.Range.Length ?? 0;
            }
        }, writeLine: Output);
    }

    private static string Mutate(string text, Mutation mutation, int position, char c)
    {
        var lines = text.Split('\n').ToList();
        var line = position % lines.Count;
        switch (mutation)
        {
            case Mutation.ReplaceChar when text.Length > 0:
                var at = position % text.Length;
                return text.Substring(0, at) + c + text.Substring(at + 1);
            case Mutation.InsertChar:
                var insert = position % (text.Length + 1);
                return text.Insert(insert, c.ToString());
            case Mutation.DeleteChar when text.Length > 0:
                return text.Remove(position % text.Length, 1);
            case Mutation.DropLine:
                lines.RemoveAt(line);
                break;
            case Mutation.RepeatLine:
                lines.Insert(line, lines[line]);
                break;
            case Mutation.SwapLines when lines.Count > 1:
                var other = (line + 1) % lines.Count;
                (lines[line], lines[other]) = (lines[other], lines[line]);
                break;
        }
        return string.Join("\n", lines);
    }

    #endregion

    #region File names

    // Characters that make names unsafe somewhere, ordinary ones, and anything at all, including lone surrogates
    private static readonly Gen<string> NameGen = Gen.Frequency(
        (3, Gen.Frequency(
                (3, Gen.Char["\"<>|:*?\\/. \t\0\u001f"]),
                (5, Gen.Char['a', 'z']),
                (1, Gen.Char),
                (1, Gen.Char["é中ا"]))
            .Array[0, 300].Select(chars => new string(chars))),
        (1, Gen.Select(Gen.OneOfConst("CON", "nul", "Com1", "LPT9", "aux", "CONIN$", "prn "), Gen.OneOfConst("", ".txt", ".tar.gz", " .log", "."), (stem, extension) => stem + extension)),
        (1, Gen.Select(Gen.OneOfConst("\U0001F600", "é", "a"), Gen.Int[1, 200], (unit, count) => string.Concat(Enumerable.Repeat(unit, count)) + ".txt")));

    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
    ];

    [Fact]
    public void A_name_from_a_source_is_always_safe_on_every_os()
    {
        NameGen.Sample(name =>
        {
            var safe = FileNames.ToSafeName(name);
            if (safe == null)
            {
                return;
            }

            Assert.NotEqual(".", safe);
            Assert.NotEqual("..", safe);
            Assert.DoesNotContain(safe, c => c < 32 || "\"<>|:*?\\/".Contains(c));
            Assert.False(safe.EndsWith('.') || safe.EndsWith(' '), $"'{safe}' ends with a dot or space");
            Assert.True(Encoding.UTF8.GetByteCount(safe) <= FileNames.MaxBytes, $"'{safe}' is longer than {FileNames.MaxBytes} bytes");
            var stem = safe.Split('.')[0].TrimEnd(' ');
            Assert.DoesNotContain(Reserved, reserved => stem.Equals(reserved, StringComparison.OrdinalIgnoreCase));
            // Shortening never splits a character in two
            if (name.Length == 0 || char.IsSurrogate(name[^1]) || !name.Any(char.IsSurrogate))
            {
                Assert.False(HasLoneSurrogate(safe) && !HasLoneSurrogate(name), $"'{safe}' has a split surrogate pair");
            }
            // The result is a single name: making it safe again changes nothing
            Assert.Equal(safe, FileNames.ToSafeName(safe));
        }, writeLine: Output);
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return true;
            }
        }
        return false;
    }

    #endregion

    #region Planning

    private static readonly Gen<(long Length, int Connections, long MinSplit, TargetCapabilities Target)> PlanGen =
        from length in Gen.Frequency((1, Gen.Long[0, 10]), (5, Gen.Long[0, 50_000_000]))
        from connections in Gen.Int[1, 16]
        from minSplit in Gen.Long[1, 5_000_000]
        from minPart in Gen.Frequency((2, Gen.Const(0L)), (1, Gen.Long[1, 5_000_000]))
        from maxPart in Gen.Frequency((2, Gen.Const((long?)null)), (1, Gen.Long[1, 20_000_000].Select(size => (long?)size)))
        from maxParts in Gen.Frequency((2, Gen.Const((int?)null)), (1, Gen.Int[1, 10_000].Select(parts => (int?)parts)))
        from multiple in Gen.Frequency((2, Gen.Const((long?)null)), (1, Gen.Long[1, 3_000_000].Select(size => (long?)size)))
        select (length, connections, minSplit, new TargetCapabilities { ParallelParts = true, MinPartSize = minPart, MaxPartSize = maxPart, MaxParts = maxParts, PartSizeMultiple = multiple });

    [Fact]
    public void Planned_parts_cover_the_content_once_within_the_targets_limits()
    {
        PlanGen.Sample(plan =>
        {
            var (length, connections, minSplit, target) = plan;
            IReadOnlyList<ByteRange> parts;
            try
            {
                parts = PartPlanner.Plan(length, supportsRanges: true, target, new TransferOptions { MaxConnections = connections, MinSplitPartSize = minSplit });
            }
            catch (TransferException ex) when (ex.Kind == TransferErrorKind.Unsupported)
            {
                // Only when no split fits the target's limits
                Assert.True(target.MaxPartSize != null && target.MaxParts != null || target.MinPartSize > 0 || target.PartSizeMultiple != null && target.MaxPartSize != null, ex.Message);
                return;
            }

            if (parts.Count == 1 && parts[0].IsAll)
            {
                return;
            }

            long next = 0;
            foreach (var part in parts)
            {
                Assert.Equal(next, part.Start);
                var size = part.Length!.Value;
                Assert.True(size > 0);
                Assert.True(target.MaxPartSize == null || size <= target.MaxPartSize, $"Part {part} is larger than {target.MaxPartSize}");
                Assert.True(target.PartSizeMultiple == null || next + size == length || size % target.PartSizeMultiple == 0, $"Part {part} isn't a multiple of {target.PartSizeMultiple}");
                next += size;
                Assert.True(next == length || size >= target.MinPartSize, $"Part {part} is smaller than {target.MinPartSize}");
            }
            Assert.Equal(length, next);
            Assert.True(target.MaxParts == null || parts.Count <= target.MaxParts);
            // No more parts than connections, unless the target's largest part makes more necessary
            // A multiple makes the largest part that fits the largest aligned one
            var largest = target.MaxPartSize is long maxPart && target.PartSizeMultiple is long aligned ? maxPart / aligned * aligned : target.MaxPartSize;
            var needed = largest is long fits && fits > 0 ? (length + fits - 1) / fits : 0;
            Assert.True(parts.Count <= Math.Max(connections, needed), $"{parts.Count} parts for {connections} connections");
        }, writeLine: Output);
    }

    #endregion
}
