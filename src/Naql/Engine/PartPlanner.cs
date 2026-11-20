using System;
using System.Collections.Generic;

namespace Naql.Engine;

/// <summary>
/// Splits content into the parts a transfer moves, one connection each.
/// </summary>
internal static class PartPlanner
{
    private static readonly IReadOnlyList<ByteRange> Single = new[] { ByteRange.All };

    /// <summary>
    /// Plans the parts for content of <paramref name="length"/> bytes.
    /// </summary>
    /// <returns>Consecutive ranges covering the content, or just <see cref="ByteRange.All"/> when it isn't split</returns>
    public static IReadOnlyList<ByteRange> Plan(long? length, bool supportsRanges, TargetCapabilities target, TransferOptions options)
    {
        return Plan(length, supportsRanges, target, options, out _);
    }

    /// <inheritdoc cref="Plan(long?, bool, TargetCapabilities, TransferOptions)"/>
    /// <param name="length">The length of the content, or <c>null</c> when unknown</param>
    /// <param name="supportsRanges">Whether the source can read ranges</param>
    /// <param name="target">What the target can take</param>
    /// <param name="options">The transfer's settings</param>
    /// <param name="reason">Why there is only one part, or <see cref="SingleConnectionReason.None"/></param>
    public static IReadOnlyList<ByteRange> Plan(long? length, bool supportsRanges, TargetCapabilities target, TransferOptions options, out SingleConnectionReason reason)
    {
        reason = length == null ? SingleConnectionReason.UnknownLength
            : length == 0 ? SingleConnectionReason.TooSmall
            : !supportsRanges ? SingleConnectionReason.NoRangeSupport
            : !target.ParallelParts || target.MaxParts == 1 ? SingleConnectionReason.TargetTakesOnePart
            : options.MaxConnections == 1 ? SingleConnectionReason.NotRequested
            : SingleConnectionReason.None;
        if (reason != SingleConnectionReason.None)
        {
            return Single;
        }
        var total = length!.Value;

        var minPartSize = Math.Max(options.MinSplitPartSize, target.MinPartSize);
        var count = Math.Min(options.MaxConnections, total / minPartSize);
        if (target.MaxPartSize is long maxPartSize)
        {
            // More parts than connections is fine: the extra parts wait for a free connection
            count = Math.Max(count, DivideRoundingUp(total, maxPartSize));
        }
        if (target.MaxParts is int maxParts && count > maxParts)
        {
            count = maxParts;
        }
        if (count <= 1)
        {
            reason = SingleConnectionReason.TooSmall;
            return Single;
        }

        var partSize = DivideRoundingUp(total, count);
        if (target.PartSizeMultiple is long multiple)
        {
            // Rounding up only makes fewer parts; if that's too large for the target, the largest aligned size that fits
            partSize = DivideRoundingUp(partSize, multiple) * multiple;
            if (target.MaxPartSize is long largest && partSize > largest)
            {
                partSize = largest / multiple * multiple;
            }
            if (partSize >= total)
            {
                reason = SingleConnectionReason.TooSmall;
                return Single;
            }
        }

        if (partSize == 0 || partSize > target.MaxPartSize || (partSize < target.MinPartSize && partSize < total) ||
            DivideRoundingUp(total, partSize) > target.MaxParts)
        {
            var multipleText = target.PartSizeMultiple is long size ? $", in multiples of {size}" : "";
            throw new TransferException(
                $"The target can't take {total} bytes in at most {target.MaxParts} parts of {target.MinPartSize} to {target.MaxPartSize} bytes{multipleText}",
                TransferErrorKind.Unsupported, side: TransferSide.Engine);
        }

        var parts = new List<ByteRange>((int)DivideRoundingUp(total, partSize));
        for (long start = 0; start < total; start += partSize)
        {
            parts.Add(new ByteRange(start, Math.Min(partSize, total - start)));
        }
        return parts;
    }

    /// <summary>
    /// Where to split a running part that covers <paramref name="start"/> to <paramref name="end"/> and can't give away
    /// the bytes before <paramref name="from"/>: halfway through the rest, at a multiple the target needs, and only when
    /// both halves are worth a connection and the target takes their sizes.
    /// </summary>
    /// <returns>Where the new part starts, or <c>null</c> when splitting isn't worth it</returns>
    public static long? SplitAt(long start, long from, long end, TargetCapabilities target, TransferOptions options)
    {
        var minSplit = options.MinSplitPartSize;
        if (end - from < 2 * minSplit)
        {
            return null;
        }

        var at = Math.Max(from + ((end - from) / 2), start + target.MinPartSize);
        if (target.PartSizeMultiple is long multiple)
        {
            // Parts start at multiples, so the new one has to as well
            at = DivideRoundingUp(at, multiple) * multiple;
        }
        return end - at < Math.Max(minSplit, target.MinPartSize) || at - from < minSplit ? null : at;
    }

    private static long DivideRoundingUp(long value, long divisor) => ((value - 1) / divisor) + 1;
}
