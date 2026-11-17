using Naql.Engine;

namespace Naql.Tests.Transfers;

public sealed class PartPlannerTests
{
    private static readonly TargetCapabilities Parallel = new() { ParallelParts = true };
    private static readonly TransferOptions Options = new() { MaxConnections = 4, MinPartSize = 1_000 };

    public static TheoryData<long?, bool, bool, int> SinglePartCases => new()
    {
        // length, source supports ranges, target takes parallel parts, connections
        { null, true, true, 4 },
        { 0, true, true, 4 },
        { 10_000, false, true, 4 },
        { 10_000, true, false, 4 },
        { 10_000, true, true, 1 },
        { 1_999, true, true, 4 },
    };

    [Theory]
    [MemberData(nameof(SinglePartCases))]
    public void Uses_one_part_when_splitting_isnt_possible_or_worth_it(long? length, bool supportsRanges, bool parallelParts, int connections)
    {
        var parts = PartPlanner.Plan(length, supportsRanges, new TargetCapabilities { ParallelParts = parallelParts }, Options with { MaxConnections = connections });

        Assert.Equal(ByteRange.All, Assert.Single(parts));
    }

    [Fact]
    public void Splits_into_one_equal_part_per_connection()
    {
        var parts = PartPlanner.Plan(10_000, true, Parallel, Options);

        Assert.Equal(new ByteRange[] { new(0, 2_500), new(2_500, 2_500), new(5_000, 2_500), new(7_500, 2_500) }, parts);
    }

    [Fact]
    public void Uses_fewer_parts_than_connections_when_parts_would_be_too_small()
    {
        var parts = PartPlanner.Plan(3_500, true, Parallel, Options with { MaxConnections = 8 });

        Assert.Equal(new ByteRange[] { new(0, 1_167), new(1_167, 1_167), new(2_334, 1_166) }, parts);
    }

    [Fact]
    public void Respects_the_targets_minimum_part_size()
    {
        var parts = PartPlanner.Plan(12_000, true, Parallel with { MinPartSize = 5_000 }, Options with { MaxConnections = 8 });

        Assert.Equal(new ByteRange[] { new(0, 6_000), new(6_000, 6_000) }, parts);
    }

    [Fact]
    public void Adds_parts_to_respect_the_targets_maximum_part_size()
    {
        var parts = PartPlanner.Plan(5_000, true, Parallel with { MaxPartSize = 1_000 }, Options with { MaxConnections = 2 });

        Assert.Equal(5, parts.Count);
        Assert.All(parts, part => Assert.Equal(1_000, part.Length));
    }

    [Fact]
    public void Fails_when_the_target_limits_cant_be_met()
    {
        var target = Parallel with { MaxPartSize = 1_000, MaxParts = 2 };

        var ex = Assert.Throws<TransferException>(() => PartPlanner.Plan(5_000, true, target, Options));

        Assert.Equal(TransferErrorKind.Unsupported, ex.Kind);
    }

    [Theory]
    [InlineData(2_000L)]
    [InlineData(9_999L)]
    [InlineData(10_001L)]
    [InlineData(123_457L)]
    public void Parts_are_consecutive_and_cover_the_content(long length)
    {
        var parts = PartPlanner.Plan(length, true, Parallel, Options);

        long next = 0;
        foreach (var part in parts)
        {
            Assert.Equal(next, part.Start);
            Assert.True(part.Length > 0);
            next += part.Length!.Value;
        }
        Assert.Equal(length, next);
    }
}
