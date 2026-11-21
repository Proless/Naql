using System;

namespace Naqel;

/// <summary>
/// A span of bytes in the content: an absolute start and an optional length.
/// </summary>
/// <remarks>
/// <c>default(ByteRange)</c> is the same as <see cref="All"/>.
/// </remarks>
public readonly struct ByteRange : IEquatable<ByteRange>
{
    /// <summary>
    /// The whole content: from the first byte to the end.
    /// </summary>
    public static ByteRange All => default;

    /// <summary>
    /// The position of the first byte.
    /// </summary>
    public long Start { get; }

    /// <summary>
    /// The number of bytes, or <c>null</c> for everything from <see cref="Start"/> to the end of the content.
    /// </summary>
    public long? Length { get; }

    /// <summary>
    /// The position of the last byte, or <c>null</c> when the range is open-ended or empty.
    /// </summary>
    public long? End => Length is > 0 ? Start + Length.Value - 1 : null;

    /// <summary>
    /// Whether this range covers the whole content.
    /// </summary>
    public bool IsAll => Start == 0 && Length is null;

    /// <summary>
    /// Creates a range from a start and an optional length.
    /// </summary>
    /// <param name="start">The position of the first byte</param>
    /// <param name="length">The number of bytes, or <c>null</c> for everything to the end of the content</param>
    public ByteRange(long start, long? length = null)
    {
        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "The start can't be negative");
        }
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The length can't be negative");
        }

        Start = start;
        Length = length;
    }

    /// <summary>
    /// Creates a range from its first and last byte, both included.
    /// </summary>
    public static ByteRange FromBounds(long start, long end)
    {
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, "The end can't be before the start");
        }

        return new ByteRange(start, end - start + 1);
    }

    /// <summary>
    /// Returns the rest of this range after the first <paramref name="count"/> bytes.
    /// </summary>
    public ByteRange Skip(long count)
    {
        if (count < 0 || count > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "The count must be between 0 and the length of the range");
        }

        return new ByteRange(Start + count, Length - count);
    }

    /// <inheritdoc />
    public bool Equals(ByteRange other) => Start == other.Start && Length == other.Length;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ByteRange other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Start, Length);

    /// <inheritdoc />
    public override string ToString() => Length switch
    {
        null => $"{Start}-",
        0 => $"{Start} (empty)",
        _ => $"{Start}-{End}"
    };

    /// <summary>
    /// Compares two ranges.
    /// </summary>
    public static bool operator ==(ByteRange left, ByteRange right) => left.Equals(right);

    /// <summary>
    /// Compares two ranges.
    /// </summary>
    public static bool operator !=(ByteRange left, ByteRange right) => !left.Equals(right);
}
