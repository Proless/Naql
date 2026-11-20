using Naql.Tests.Fakes;

namespace Naql.Tests.Transfers;

/// <summary>
/// One content made of ordered pieces, read whole, in parallel parts across piece boundaries, or by range.
/// </summary>
public sealed class ConcatenatedSourceTests
{
    private static readonly TransferOptions Options = new()
    {
        MinSplitPartSize = 1_000,
        BufferSize = 700,
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false }
    };

    private static readonly TransferOptions Parallel = Options with { MaxConnections = 4 };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Uneven pieces, one of them empty, so parts start and end inside pieces
    private static readonly int[] Sizes = [3_000, 1, 0, 4_500, 2_499];

    private static (MemorySource[] Pieces, byte[] Content) Pieces(params int[] sizes)
    {
        var random = new Random(9);
        var pieces = sizes.Select((size, i) =>
        {
            var bytes = new byte[size];
            random.NextBytes(bytes);
            return new MemorySource(bytes) { Identity = $"memory://piece/{i}" };
        }).ToArray();
        return (pieces, pieces.SelectMany(piece => piece.Content).ToArray());
    }

    private static Task<TransferResult> RunAsync(ITransferSource source, MemoryTarget target, TransferOptions options)
    {
        return new TransferClient(options).From(source).To(target).RunAsync(cancellationToken: Token);
    }

    [Fact]
    public async Task The_pieces_read_whole_make_the_content()
    {
        var (pieces, content) = Pieces(Sizes);
        var source = ConcatenatedSource.From(pieces, name: "video.ts");
        var target = new MemoryTarget();

        var result = await RunAsync(source, target, Options);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(content.Length, result.Source.Length);
        Assert.Equal("video.ts", result.Source.Name);
        Assert.NotNull(result.Source.Version);
        Assert.NotNull(result.Source.Identity);
        Assert.True(result.Source.SupportsRanges);
    }

    [Fact]
    public async Task Parallel_parts_read_across_piece_boundaries()
    {
        var (pieces, content) = Pieces(Sizes);
        var target = new MemoryTarget();

        var result = await RunAsync(ConcatenatedSource.From(pieces), target, Parallel);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(4, result.Parts);
    }

    [Theory]
    [InlineData(0, 10_000)]
    [InlineData(2_999, 3)]
    [InlineData(3_001, 4_500)]
    [InlineData(5_000, 1)]
    [InlineData(9_999, 1)]
    public async Task A_range_has_exactly_its_bytes_from_the_pieces_it_covers(long start, long length)
    {
        var (pieces, content) = Pieces(Sizes);
        var source = ConcatenatedSource.From(pieces);
        await source.GetInfoAsync(Token);

        using var read = await source.OpenReadAsync(new ReadRequest(new ByteRange(start, length)), Token);
        var copy = new MemoryStream();
        await read.Content.CopyToAsync(copy, 81920, Token);

        Assert.Equal(content.Skip((int)start).Take((int)length).ToArray(), copy.ToArray());
    }

    [Fact]
    public async Task Pieces_of_unknown_length_are_read_one_after_another()
    {
        var (pieces, content) = Pieces(Sizes);
        var unknown = pieces.Select(piece => new MemorySource(piece.Content) { ReportsLength = false, SupportsRanges = false }).ToArray();
        var target = new MemoryTarget();

        var result = await RunAsync(ConcatenatedSource.From(unknown), target, Parallel);

        Assert.Equal(content, target.Session.Data);
        Assert.Null(result.Source.Length);
        Assert.Equal(SingleConnectionReason.UnknownLength, result.SingleConnectionReason);
    }

    [Fact]
    public async Task A_piece_without_a_version_leaves_the_content_without_one()
    {
        var (pieces, content) = Pieces(Sizes);
        pieces[1].Version = null;
        var target = new MemoryTarget();

        var result = await RunAsync(ConcatenatedSource.From(pieces), target, Parallel);

        Assert.Equal(content, target.Session.Data);
        Assert.Null(result.Source.Version);
        Assert.Equal(SingleConnectionReason.NoVersion, result.SingleConnectionReason);
    }

    [Fact]
    public async Task A_piece_that_ends_early_is_retried_instead_of_shifting_the_rest()
    {
        var (pieces, content) = Pieces(Sizes);
        var cut = 0;
        pieces[3] = new MemorySource(pieces[3].Content)
        {
            Identity = pieces[3].Identity,
            // The first read of this piece ends after 1 000 bytes without an error
            WrapContent = (_, _, stream) => Interlocked.Exchange(ref cut, 1) == 0 ? new MemoryStream(ReadSome(stream, 1_000)) : stream
        };
        var target = new MemoryTarget();

        var result = await RunAsync(ConcatenatedSource.From(pieces), target, Options);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(1, result.Retries);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task A_piece_that_changed_starts_the_transfer_over_with_the_pieces_as_they_are_now(int connections)
    {
        var (pieces, _) = Pieces(Sizes);
        var changed = new byte[pieces[3].Content.Length];
        new Random(1).NextBytes(changed);
        var opened = 0;
        pieces[3] = new MemorySource(pieces[3].Content)
        {
            Identity = pieces[3].Identity,
            // The piece changes right after the transfer described it
            OnOpen = (_, _, _) =>
            {
                if (Interlocked.Exchange(ref opened, 1) == 0)
                {
                    pieces[3].Content = changed;
                    pieces[3].Version = "v2";
                }
                return Task.CompletedTask;
            }
        };
        var target = new MemoryTarget();

        var result = await RunAsync(ConcatenatedSource.From(pieces), target, Options with { MaxConnections = connections });

        // The new content of the piece, and a transfer that started over once to get it
        Assert.Same(changed, pieces[3].Content);
        Assert.Equal(pieces.SelectMany(piece => piece.Content).ToArray(), target.Sessions[^1].Data);
        Assert.Equal(1, result.Retries);
    }

    [Fact]
    public async Task No_pieces_make_empty_content()
    {
        var target = new MemoryTarget();

        var result = await RunAsync(ConcatenatedSource.From([]), target, Parallel);

        Assert.Empty(target.Session.Data);
        Assert.Equal(0, result.Source.Length);
    }

    [Fact]
    public async Task The_strictest_piece_limits_the_connections()
    {
        var (pieces, content) = Pieces(Sizes);
        pieces[0] = new MemorySource(pieces[0].Content) { MaxConnections = 2 };
        var target = new MemoryTarget();

        var result = await RunAsync(ConcatenatedSource.From(pieces), target, Parallel);

        Assert.Equal(content, target.Session.Data);
        Assert.Equal(2, result.Parts);
    }

    private static byte[] ReadSome(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        int n;
        while (read < count && (n = stream.Read(buffer, read, count - read)) > 0)
        {
            read += n;
        }
        return buffer.Take(read).ToArray();
    }
}
