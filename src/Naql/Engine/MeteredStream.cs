using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Engine;

/// <summary>
/// The stream a target reads a part from. It wraps the source's stream to throttle, count bytes, record activity for
/// the idle timeout, stop at the end of the part, and notice when the source ends too early. The end can move earlier
/// while the part runs, when the engine gives the rest of the part to another one.
/// </summary>
internal sealed class MeteredStream : Stream
{
    private readonly Stream _inner;
    private readonly object _lengthLock = new();
    private long? _expectedLength;
    private long _reservedEnd;
    private readonly int _maxRead;
    private readonly BandwidthLimiter[] _limiters;
    private readonly Action<ReadOnlyMemory<byte>> _onRead;
    private readonly Action<long> _onConfirmed;
    private readonly Stopwatch _clock;
    private long _read;
    private long _lastActivityTicks;
    private long _endedAtTicks;
    private int _reachedEnd;
    private int _readPending;
    private int _throttling;
    private Exception? _readFailure;

    /// <param name="inner">The source's stream</param>
    /// <param name="expectedLength">How many bytes the part has, or <c>null</c> to read until the source ends</param>
    /// <param name="maxRead">The most bytes to read from the source at once</param>
    /// <param name="limiters">The rate limits to wait for after each read</param>
    /// <param name="onRead">Called with the bytes of each read, before the target gets them</param>
    /// <param name="onConfirmed">Called with the bytes read so far whenever the target asks for more: it has handled
    /// everything before, so a checkpoint can count those bytes as kept</param>
    /// <param name="clock">The clock activity is measured with</param>
    public MeteredStream(Stream inner, long? expectedLength, int maxRead, BandwidthLimiter[] limiters, Action<ReadOnlyMemory<byte>> onRead, Action<long> onConfirmed, Stopwatch clock)
    {
        _inner = inner;
        _expectedLength = expectedLength;
        _maxRead = maxRead;
        _limiters = limiters;
        _onRead = onRead;
        _onConfirmed = onConfirmed;
        _clock = clock;
        Touch();
    }

    /// <summary>
    /// The bytes this stream can no longer give away: those it read, and those a read in flight may still return.
    /// </summary>
    public long ReservedEnd
    {
        get
        {
            lock (_lengthLock)
            {
                return _reservedEnd;
            }
        }
    }

    /// <summary>
    /// Ends the stream sooner, after <paramref name="length"/> bytes, so another part can move the rest.
    /// </summary>
    /// <returns><c>false</c>, changing nothing, when the length isn't known, isn't shorter, the stream already ended,
    /// or it read or may still read past <paramref name="length"/></returns>
    public bool TryEndAt(long length)
    {
        lock (_lengthLock)
        {
            if (_expectedLength is not long expected || length >= expected || length < _reservedEnd || ReachedEnd)
            {
                return false;
            }
            _expectedLength = length;
            return true;
        }
    }

    /// <summary>
    /// The bytes handed to the target.
    /// </summary>
    public long BytesRead => Interlocked.Read(ref _read);

    /// <summary>
    /// Whether the target read the whole part.
    /// </summary>
    public bool ReachedEnd => Volatile.Read(ref _reachedEnd) == 1;

    /// <summary>
    /// When the target read the whole part, on the clock; from then on, it's only finishing the part.
    /// </summary>
    public TimeSpan EndedAt => TimeSpan.FromTicks(Interlocked.Read(ref _endedAtTicks));

    /// <summary>
    /// The error reading from the source failed with, if it did. It tells source failures apart from target failures.
    /// </summary>
    public Exception? ReadFailure => Volatile.Read(ref _readFailure);

    /// <summary>
    /// Whether the target is waiting for the source right now.
    /// </summary>
    public bool ReadPending => Volatile.Read(ref _readPending) == 1;

    /// <summary>
    /// Whether the stream is waiting for the speed limit, which doesn't count as being idle.
    /// </summary>
    public bool Throttling => Volatile.Read(ref _throttling) == 1;

    /// <summary>
    /// When bytes last moved, on <see cref="Stopwatch.Elapsed"/> of the clock.
    /// </summary>
    public TimeSpan LastActivity => TimeSpan.FromTicks(Interlocked.Read(ref _lastActivityTicks));

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => BytesRead;
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
        if (buffer.IsEmpty)
        {
            return 0;
        }

        // A target reads again once it has written what it read before
        _onConfirmed(_read);
        if (ReachedEnd)
        {
            return 0;
        }

        var toRead = Math.Min(buffer.Length, _maxRead);
        lock (_lengthLock)
        {
            if (_expectedLength is long expected)
            {
                var remaining = expected - _read;
                if (remaining == 0)
                {
                    // Don't read past the part, even when the source has more
                    MarkEnd();
                    return 0;
                }
                toRead = (int)Math.Min(toRead, remaining);
            }
            // The end can't move before what this read may return
            _reservedEnd = _read + toRead;
        }

        int read;
        Volatile.Write(ref _readPending, 1);
        try
        {
            read = await _inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _readFailure, ex, null);
            throw;
        }
        finally
        {
            Volatile.Write(ref _readPending, 0);
        }
        Touch();

        if (read == 0)
        {
            long? expectedLength;
            lock (_lengthLock)
            {
                expectedLength = _expectedLength;
            }
            if (expectedLength is long length && _read < length)
            {
                var failure = new TransferException($"The source ended after {_read} of {length} bytes", TransferErrorKind.Network, isTransient: true);
                Interlocked.CompareExchange(ref _readFailure, failure, null);
                throw failure;
            }
            MarkEnd();
            return 0;
        }

        Interlocked.Add(ref _read, read);
        _onRead(buffer[..read]);

        if (_limiters.Length > 0)
        {
            Volatile.Write(ref _throttling, 1);
            try
            {
                foreach (var limiter in _limiters)
                {
                    await limiter.WaitAsync(read, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                Touch();
                Volatile.Write(ref _throttling, 0);
            }
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Throttling and timing are asynchronous, so a synchronous read waits for the asynchronous one
        Stream.ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
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
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, _clock.Elapsed.Ticks);

    private void MarkEnd()
    {
        if (!ReachedEnd)
        {
            Interlocked.Exchange(ref _endedAtTicks, _clock.Elapsed.Ticks);
            Volatile.Write(ref _reachedEnd, 1);
        }
    }
}
