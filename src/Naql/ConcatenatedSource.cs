using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Naql;

/// <summary>
/// One content made of ordered pieces, each a source of its own: the segments of a stream such as HLS, the volumes of
/// a split archive, the chunks a service stores a file in.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>It describes itself by asking every piece once, so a source of many pieces makes as many requests first.</item>
/// <item>When every piece reports its length and supports ranges, the content does too, so it can be split into parts
/// and resumed; a range is read from the pieces it covers. Otherwise it's read from start to end.</item>
/// <item>Its version combines the pieces' versions, and is only known when every piece has one; each piece is read
/// with its own version, so a piece that changed fails as <see cref="TransferErrorKind.SourceChanged"/>.</item>
/// <item>A piece that ends before its length fails the read, instead of shifting the bytes of the pieces after it.</item>
/// <item>Pieces are opened as the read reaches them, so the idle timeout, not the response timeout, covers opening
/// every piece but the first.</item>
/// </list>
/// </remarks>
[SuppressMessage("Design", "CA1001", Justification = "The lock never creates a wait handle, so it holds nothing to release, and a source is a description that isn't disposed")]
public sealed class ConcatenatedSource : ITransferSource
{
    private readonly ITransferSource[] _pieces;
    private readonly string? _name;
    private readonly string? _identity;
    private readonly SemaphoreSlim _describing = new(1, 1);
    private volatile Described? _described;

    private ConcatenatedSource(ITransferSource[] pieces, string? name, string? identity)
    {
        _pieces = pieces;
        _name = name;
        _identity = identity;
    }

    /// <summary>
    /// The content of <paramref name="pieces"/>, one after another.
    /// </summary>
    /// <param name="pieces">The pieces, in order</param>
    /// <param name="name">A suggested file name for the whole content</param>
    /// <param name="identity">What the whole content is, for <see cref="SourceInfo.Identity"/>; by default it's made
    /// from the pieces' identities, when they all have one</param>
    public static ConcatenatedSource From(IEnumerable<ITransferSource> pieces, string? name = null, string? identity = null)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        var array = pieces.ToArray();
        if (array.Any(piece => piece == null))
        {
            throw new ArgumentException("A piece is null", nameof(pieces));
        }
        return new ConcatenatedSource(array, name, identity);
    }

    /// <summary>
    /// The pieces, in order.
    /// </summary>
    public IReadOnlyList<ITransferSource> Pieces => _pieces;

    /// <inheritdoc />
    public async Task<SourceInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        return (await DescribeAsync(cancellationToken, fresh: true).ConfigureAwait(false)).Info;
    }

    /// <inheritdoc />
    public async Task<SourceRead> OpenReadAsync(ReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var described = await DescribeAsync(cancellationToken).ConfigureAwait(false);
        var info = described.Info;
        if (request.IfVersion != null && info.Version != null && request.IfVersion != info.Version)
        {
            throw new TransferException($"The content changed from version {request.IfVersion} to {info.Version}", TransferErrorKind.SourceChanged, side: TransferSide.Source);
        }
        if (!request.Range.IsAll && !info.SupportsRanges)
        {
            throw new TransferException($"The pieces can't be read from the bytes {request.Range}, since not all of them report a length or support ranges",
                TransferErrorKind.Unsupported, side: TransferSide.Source);
        }

        var reads = Plan(described, request.Range);
        var stream = new ConcatenatedStream(reads, () => _described = null);
        // The first piece opens now, so a failure to open the content shows here, within the response timeout
        await stream.OpenNextAsync(cancellationToken).ConfigureAwait(false);
        return new SourceRead(stream, info);
    }

    /// <summary>
    /// The reads of the pieces a range covers, in order.
    /// </summary>
    private List<PieceRead> Plan(Described described, ByteRange range)
    {
        var reads = new List<PieceRead>();
        if (range.IsAll && described.Info.Length == null)
        {
            // Lengths unknown: every piece whole, one after another
            for (var i = 0; i < _pieces.Length; i++)
            {
                reads.Add(new PieceRead(_pieces[i], ByteRange.All, described.Pieces[i].Length, described.Pieces[i].Version));
            }
            return reads;
        }

        var start = range.Start;
        var end = range.Length is long length ? range.Start + length : described.Info.Length!.Value;
        long offset = 0;
        for (var i = 0; i < _pieces.Length && offset < end; i++)
        {
            var pieceLength = described.Pieces[i].Length!.Value;
            var pieceEnd = offset + pieceLength;
            if (pieceEnd > start && pieceLength > 0)
            {
                var from = Math.Max(start, offset) - offset;
                var to = Math.Min(end, pieceEnd) - offset;
                var whole = from == 0 && to == pieceLength;
                reads.Add(new PieceRead(_pieces[i], whole ? ByteRange.All : new ByteRange(from, to - from), to - from, described.Pieces[i].Version));
            }
            offset = pieceEnd;
        }
        return reads;
    }

    /// <summary>
    /// What the pieces reported: the last description, unless <paramref name="fresh"/> asks the pieces again. A piece
    /// that changed forgets the last description, so starting over describes the pieces as they are now.
    /// </summary>
    private async Task<Described> DescribeAsync(CancellationToken cancellationToken, bool fresh = false)
    {
        if (!fresh && _described is { } described)
        {
            return described;
        }

        await _describing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!fresh && _described is { } again)
            {
                return again;
            }

            var pieces = new SourceInfo[_pieces.Length];
            for (var i = 0; i < _pieces.Length; i++)
            {
                pieces[i] = await _pieces[i].GetInfoAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new TransferException($"Piece {i} didn't describe its content", TransferErrorKind.Protocol, side: TransferSide.Source);
            }

            var lengthsKnown = pieces.All(piece => piece.Length != null);
            var info = new SourceInfo
            {
                Length = lengthsKnown ? pieces.Sum(piece => piece.Length!.Value) : null,
                SupportsRanges = lengthsKnown && pieces.All(piece => piece.SupportsRanges),
                IsRepeatable = pieces.All(piece => piece.IsRepeatable),
                MaxConnections = pieces.Select(piece => piece.MaxConnections).Where(limit => limit != null).Min(),
                Version = Combine("versions", pieces.Select(piece => piece.Version)),
                Identity = _identity ?? Combine("identities", pieces.Select(piece => piece.Identity)),
                Name = _name,
                LastModified = pieces.Select(piece => piece.LastModified).Where(date => date != null).Max()
            };
            _described = new Described(info, pieces);
            return _described;
        }
        finally
        {
            _describing.Release();
        }
    }

    /// <summary>
    /// A short value that stands for all the pieces' values together, or <c>null</c> when a piece has none.
    /// </summary>
    private static string? Combine(string what, IEnumerable<string?> values)
    {
        var all = values.ToList();
        return all.Any(value => value == null) ? null : $"concatenated {what} " + TransferCheckpoint.Fingerprint(string.Join("\n", all));
    }

    private sealed record Described(SourceInfo Info, SourceInfo[] Pieces);

    private sealed record PieceRead(ITransferSource Source, ByteRange Range, long? Expected, string? Version);

    /// <summary>
    /// Reads the pieces of a range one after another, opening each as the read reaches it.
    /// </summary>
    private sealed class ConcatenatedStream(List<PieceRead> reads, Action changed) : Stream
    {
        private int _next;
        private SourceRead? _current;
        private long? _expected;
        private long _readFromCurrent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Opens the next piece; returns <c>false</c> when there is none.
        /// </summary>
        public async Task<bool> OpenNextAsync(CancellationToken cancellationToken)
        {
            _current?.Dispose();
            _current = null;
            if (_next >= reads.Count)
            {
                return false;
            }

            var read = reads[_next++];
            try
            {
                _current = await read.Source.OpenReadAsync(new ReadRequest(read.Range, read.Version), cancellationToken).ConfigureAwait(false);
            }
            catch (TransferException ex) when (ex.Kind == TransferErrorKind.SourceChanged)
            {
                changed();
                throw;
            }
            _expected = read.Expected;
            _readFromCurrent = 0;
            return true;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Stream.ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

#if NETSTANDARD2_0
        // netstandard2.0's Stream has no Memory overloads; the array overloads call this one
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
#else
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
#endif
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            while (_current != null)
            {
                var toRead = _expected is long expected ? (int)Math.Min(buffer.Length, expected - _readFromCurrent) : buffer.Length;
                var read = toRead == 0 ? 0 : await _current.Content.ReadAsync(buffer.Slice(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read > 0)
                {
                    _readFromCurrent += read;
                    return read;
                }
                if (_expected is long length && _readFromCurrent < length)
                {
                    throw new TransferException($"Piece {_next - 1} ended after {_readFromCurrent} of {length} bytes", TransferErrorKind.Network, isTransient: true, side: TransferSide.Source);
                }
                await OpenNextAsync(cancellationToken).ConfigureAwait(false);
            }
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Stream.ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _current?.Dispose();
                _current = null;
            }
            base.Dispose(disposing);
        }
    }
}
