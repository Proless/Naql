using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Streams;

/// <summary>
/// Writes into a stream the caller owns. The stream is flushed when the transfer completes, never disposed. The target wraps
/// that one stream, so it belongs to one transfer.
/// </summary>
/// <remarks>
/// A seekable stream takes parallel parts: writes seek before every write, one at a time. A stream that can't seek is
/// written in order from its current position; a part can continue where it stopped, but nothing can be written twice.
/// Nothing written can be undone, so the target isn't resumable and aborting leaves the stream as it is.
/// </remarks>
[SuppressMessage("Design", "CA1001", Justification = "The gate never creates a wait handle, so it holds nothing to release, and an endpoint is a description that isn't disposed")]
public sealed class StreamTarget : ITransferTarget
{

    private readonly Stream _stream;
    private readonly long _origin;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _written;

    /// <summary>
    /// Writes into <paramref name="stream"/> from its current position.
    /// </summary>
    /// <param name="stream">The stream to write; it must be writable</param>
    public static StreamTarget To(Stream stream) => new(stream);

    private StreamTarget(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream can't be written", nameof(stream));
        }

        _origin = stream.CanSeek ? stream.Position : 0;
        Capabilities = new TargetCapabilities
        {
            ParallelParts = stream.CanSeek,
            ResumableParts = true,
            Verifiable = stream.CanSeek && stream.CanRead
        };
    }

    /// <inheritdoc />
    public TargetCapabilities Capabilities { get; }

    /// <inheritdoc />
    public Task<ITargetSession> OpenAsync(TargetOpenRequest request, CancellationToken cancellationToken)
    {
        if (!_stream.CanSeek && Interlocked.Read(ref _written) > 0)
        {
            throw new TransferException("A stream that can't seek can't start over", TransferErrorKind.Unsupported, side: TransferSide.Target);
        }
        return Task.FromResult<ITargetSession>(new StreamSession(this, request.BufferSize));
    }

    private sealed class StreamSession(StreamTarget target, int bufferSize) : ITargetSession, IVerifiableSession
    {
        public string? ResumeToken => null;

        // The caller has the stream, so there is no other place to point at
        public string? Location => null;

        public IReadOnlyDictionary<string, string> Metadata { get; } = new Dictionary<string, string>();

        public async Task WritePartAsync(ByteRange range, Stream content, CancellationToken cancellationToken)
        {
            var stream = target._stream;
            if (!stream.CanSeek && range.Start != Interlocked.Read(ref target._written))
            {
                throw new TransferException("A stream that can't seek can only be written in order, once", TransferErrorKind.Unsupported, side: TransferSide.Target);
            }

            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                var position = target._origin + range.Start;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target._gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (stream.CanSeek)
                        {
                            stream.Position = position;
                        }
                        await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        target._gate.Release();
                    }

                    position += read;
                    Interlocked.Add(ref target._written, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public Task CompleteAsync(CancellationToken cancellationToken) => target._stream.FlushAsync(cancellationToken);

        public Task AbortAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<byte[]> ComputeHashAsync(HashAlgorithmName algorithm, long length, CancellationToken cancellationToken)
        {
            var stream = target._stream;
            if (!stream.CanSeek || !stream.CanRead)
            {
                throw new TransferException("A stream that can't seek and read can't be read back", TransferErrorKind.Unsupported, side: TransferSide.Target);
            }

            await target._gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                stream.Position = target._origin;
                return await Hashing.HashAsync(stream, algorithm, length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                target._gate.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            // The caller owns the stream
            return default;
        }
    }
}
