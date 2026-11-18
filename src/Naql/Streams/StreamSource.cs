using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Streams;

/// <summary>
/// Reads a stream the caller owns. The stream is never disposed by the transfer. The source wraps that one stream, so it
/// belongs to one transfer.
/// </summary>
/// <remarks>
/// A seekable stream supports ranges and retries: reads seek before every read, one at a time, so parallel parts can
/// share it. A stream that can't seek is read once from its current position; it can't be retried or split.
/// </remarks>
public sealed class StreamSource : ITransferSource
{
    private readonly Stream _stream;
    private readonly long _origin;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _opened;

    /// <summary>
    /// Reads <paramref name="stream"/> from its current position.
    /// </summary>
    /// <param name="stream">The stream to read</param>
    /// <param name="name">A suggested file name for the content</param>
    /// <param name="length">The number of bytes to read; by default the rest of a seekable stream, or everything until a
    /// stream that can't seek ends</param>
    public StreamSource(Stream stream, string? name = null, long? length = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream can't be read", nameof(stream));
        }
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The length can't be negative");
        }

        if (stream.CanSeek)
        {
            _origin = stream.Position;
            length ??= stream.Length - _origin;
        }

        Info = new SourceInfo
        {
            Length = length,
            SupportsRanges = stream.CanSeek,
            IsRepeatable = stream.CanSeek,
            Name = name
        };
    }

    /// <summary>
    /// What the transfer is told about the content.
    /// </summary>
    public SourceInfo Info { get; }

    /// <inheritdoc />
    public Task<SourceInfo> GetInfoAsync(CancellationToken cancellationToken) => Task.FromResult(Info);

    /// <inheritdoc />
    public Task<SourceStream> OpenReadAsync(ReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var range = request.Range;

        if (!_stream.CanSeek)
        {
            if (!range.IsAll)
            {
                throw new TransferException("A stream that can't seek can only be read from its start", TransferErrorKind.Unsupported, side: TransferSide.Source);
            }
            if (Interlocked.Exchange(ref _opened, 1) == 1)
            {
                throw new TransferException("A stream that can't seek can only be read once", TransferErrorKind.Unsupported, side: TransferSide.Source);
            }
            return Task.FromResult(new SourceStream(new StreamSlice(_stream, Info.Length, ownsInner: false), Info));
        }

        var remaining = Info.Length!.Value - range.Start;
        if (remaining < 0)
        {
            throw new TransferException($"The range {range} starts after the end of the stream", TransferErrorKind.Protocol, side: TransferSide.Source);
        }
        var length = range.Length is long requested ? Math.Min(requested, remaining) : remaining;
        var slice = new StreamSlice(_stream, length, ownsInner: false, _origin + range.Start, _gate);
        return Task.FromResult(new SourceStream(slice, Info));
    }
}
