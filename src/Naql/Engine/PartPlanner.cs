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
        if (partSize > target.MaxPartSize || (partSize < target.MinPartSize && partSize < total))
        {
            throw new TransferException(
                $"The target can't take {total} bytes in at most {target.MaxParts} parts of {target.MinPartSize} to {target.MaxPartSize} bytes",
                TransferErrorKind.Unsupported, side: TransferSide.Engine);
        }

        var parts = new List<ByteRange>((int)count);
        for (long start = 0; start < total; start += partSize)
        {
            parts.Add(new ByteRange(start, Math.Min(partSize, total - start)));
        }
        return parts;
    }

    private static long DivideRoundingUp(long value, long divisor) => ((value - 1) / divisor) + 1;
}
