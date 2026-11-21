using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Naqel.Streams;

/// <summary>
/// A read-only view of part of another stream.
/// </summary>
internal sealed class StreamSlice : Stream
{
    private readonly Stream _inner;
    private readonly bool _ownsInner;
    private readonly SemaphoreSlim? _gate;
    private long? _position;
    private long? _remaining;

    /// <param name="inner">The stream to read from</param>
    /// <param name="remaining">How many bytes to read at most, or <c>null</c> for everything</param>
    /// <param name="ownsInner">Whether disposing the slice disposes <paramref name="inner"/></param>
    /// <param name="position">When set, every read seeks here first, so several slices can share one seekable stream</param>
    /// <param name="gate">Serializes the seek and read when slices share a stream</param>
    public StreamSlice(Stream inner, long? remaining, bool ownsInner, long? position = null, SemaphoreSlim? gate = null)
    {
        _inner = inner;
        _remaining = remaining;
        _ownsInner = ownsInner;
        _position = position;
        _gate = gate;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
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
        buffer = Limit(buffer);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        int read;
        if (_gate != null)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            if (_position is long position)
            {
                _inner.Position = position;
            }
            read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate?.Release();
        }

        return Advance(read);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Stream.ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

#if NETSTANDARD2_0
    public int Read(Span<byte> buffer)
#else
    public override int Read(Span<byte> buffer)
#endif
    {
        buffer = _remaining is long remaining && remaining < buffer.Length ? buffer[..(int)remaining] : buffer;
        if (buffer.IsEmpty)
        {
            return 0;
        }

        int read;
        _gate?.Wait();
        try
        {
            if (_position is long position)
            {
                _inner.Position = position;
            }
            read = _inner.Read(buffer);
        }
        finally
        {
            _gate?.Release();
        }

        return Advance(read);
    }

    private Memory<byte> Limit(Memory<byte> buffer)
    {
        return _remaining is long remaining && remaining < buffer.Length ? buffer[..(int)remaining] : buffer;
    }

    private int Advance(int read)
    {
        _position += read;
        _remaining -= read;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsInner)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
