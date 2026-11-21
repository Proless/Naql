namespace Naqel.Core.Tests.Transfers;

public sealed class ByteRangeTests
{
    [Fact]
    public void Default_is_the_whole_content()
    {
        Assert.Equal(ByteRange.All, default);
        Assert.True(ByteRange.All.IsAll);
        Assert.Equal(0, ByteRange.All.Start);
        Assert.Null(ByteRange.All.Length);
        Assert.Null(ByteRange.All.End);
    }

    [Theory]
    [InlineData(0L, 10L, 9L)]
    [InlineData(5L, 1L, 5L)]
    public void End_is_the_last_byte(long start, long length, long end)
    {
        Assert.Equal(end, new ByteRange(start, length).End);
    }

    [Fact]
    public void An_empty_range_has_no_end()
    {
        Assert.Null(new ByteRange(5, 0).End);
    }

    [Fact]
    public void FromBounds_includes_both_ends()
    {
        Assert.Equal(new ByteRange(10, 11), ByteRange.FromBounds(10, 20));
    }

    [Fact]
    public void Rejects_invalid_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ByteRange(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ByteRange(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ByteRange.FromBounds(5, 4));
    }

    [Fact]
    public void Skip_returns_the_rest_of_the_range()
    {
        Assert.Equal(new ByteRange(13, 7), new ByteRange(10, 10).Skip(3));
        Assert.Equal(new ByteRange(20, 0), new ByteRange(10, 10).Skip(10));
        Assert.Equal(new ByteRange(3), ByteRange.All.Skip(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ByteRange(10, 10).Skip(11));
        Assert.Throws<ArgumentOutOfRangeException>(() => ByteRange.All.Skip(-1));
    }

    [Fact]
    public void Ranges_with_the_same_start_and_length_are_equal()
    {
        Assert.True(new ByteRange(1, 2) == new ByteRange(1, 2));
        Assert.True(new ByteRange(1, 2) != new ByteRange(1));
        Assert.Equal(new ByteRange(1, 2).GetHashCode(), new ByteRange(1, 2).GetHashCode());
    }
}
