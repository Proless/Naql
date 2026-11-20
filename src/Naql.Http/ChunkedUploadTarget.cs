using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Http;

/// <summary>
/// A base for uploads to services that take content in chunks through an upload session, such as Google Drive's
/// resumable uploads, OneDrive's upload sessions, Dropbox's upload sessions and tus. A package supplies the steps:
/// start a session, send a chunk, ask where a session stands, and optionally finish or cancel it. The base reads the
/// content into chunks, sends them in order, sends again what the server didn't take, and tells the engine what the
/// server acknowledged, so a failed transfer continues from there, also from a checkpoint in a later run.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The content goes up in one part, in order, the way upload sessions take it.</item>
/// <item>Every chunk but the last has <see cref="ChunkSize"/> bytes, unless the server takes less of one; what it
/// didn't take goes again with the next chunk.</item>
/// <item>The session's <see cref="ChunkedUpload.Token"/> is the resume token. A session that is gone or expired when
/// a later run asks for it, which <see cref="QueryAsync"/> reports as <see cref="TransferErrorKind.NotFound"/> or
/// <see cref="TransferErrorKind.Expired"/>, makes the transfer start over with a new one.</item>
/// <item>Steps send their requests with <see cref="Transport"/>, so they get its settings, and turn unsuccessful
/// answers into errors with <see cref="HttpTransferException.FromResponse(System.Net.Http.HttpResponseMessage, TransferSide)"/>.</item>
/// </list>
/// </remarks>
public abstract class ChunkedUploadTarget : ITransferTarget
{
    private TargetCapabilities? _capabilities;

    /// <summary>
    /// A target that sends chunks of <paramref name="chunkSize"/> bytes with <paramref name="transport"/>.
    /// </summary>
    /// <param name="transport">The client and request settings for the steps</param>
    /// <param name="chunkSize">The bytes in every chunk but the last, such as a multiple of 320 KiB for OneDrive</param>
    protected ChunkedUploadTarget(HttpTransport transport, int chunkSize)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        ChunkSize = chunkSize;
    }

    /// <summary>
    /// The client and request settings the steps send their requests with.
    /// </summary>
    public HttpTransport Transport { get; }

    /// <summary>
    /// The bytes in every chunk but the last.
    /// </summary>
    public int ChunkSize { get; }

    /// <summary>
    /// One part, in order; continued from what the server acknowledged; resumable from the session's token; and safe
    /// to send again, since a chunk always goes to the position the server asked for.
    /// </summary>
    public TargetCapabilities Capabilities => _capabilities ??= new TargetCapabilities
    {
        ResumableParts = true,
        Resumable = true,
        RepeatableWrites = true,
        RequiresLength = RequiresLength
    };

    /// <summary>
    /// Whether the service needs the content's length before the upload starts. <c>false</c> by default: the last
    /// chunk carries the length.
    /// </summary>
    protected virtual bool RequiresLength => false;

    /// <inheritdoc />
    public async Task<ITargetSession> OpenAsync(TargetOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        long acknowledged;
        ChunkedUpload upload;
        if (request.ResumeToken is string token)
        {
            upload = new ChunkedUpload(request.Source, token);
            acknowledged = await QueryAsync(upload, cancellationToken).ConfigureAwait(false);
            if (acknowledged < 0 || acknowledged > request.Source.Length)
            {
                throw new TransferException($"The service says the upload is at {acknowledged}, outside the content", TransferErrorKind.Protocol, side: TransferSide.Target);
            }
        }
        else
        {
            upload = new ChunkedUpload(request.Source, null);
            await StartAsync(upload, cancellationToken).ConfigureAwait(false);
            if (upload.Token == null)
            {
                throw new InvalidOperationException($"{GetType().Name}.StartAsync didn't set the upload's Token");
            }
            acknowledged = 0;
        }
        return new Session(this, upload, acknowledged);
    }

    /// <summary>
    /// Starts an upload session with the service and sets <see cref="ChunkedUpload.Token"/> to what identifies it,
    /// such as the upload URL the service answered with.
    /// </summary>
    protected abstract Task StartAsync(ChunkedUpload upload, CancellationToken cancellationToken);

    /// <summary>
    /// Sends <paramref name="chunk"/> and returns the position the server has after it, which is
    /// <c>chunk.Offset + chunk.Count</c> when it took all of the chunk, and less when it took only some.
    /// </summary>
    /// <exception cref="TransferException">The chunk failed; a transient error is sent again from what the server has</exception>
    protected abstract Task<long> SendChunkAsync(ChunkedUpload upload, UploadChunk chunk, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the service how many bytes of the session <see cref="ChunkedUpload.Token"/> names it has, for a transfer
    /// that continues from a checkpoint.
    /// </summary>
    /// <exception cref="TransferException">The session is gone (<see cref="TransferErrorKind.NotFound"/>) or expired
    /// (<see cref="TransferErrorKind.Expired"/>), so the transfer starts over</exception>
    protected abstract Task<long> QueryAsync(ChunkedUpload upload, CancellationToken cancellationToken);

    /// <summary>
    /// Finishes the upload once the server has every byte, for services with a separate step, such as Dropbox's
    /// <c>finish</c>; it can set <see cref="ChunkedUpload.Location"/> and <see cref="ChunkedUpload.Metadata"/>. Does
    /// nothing by default, for services the last chunk finishes.
    /// </summary>
    protected virtual Task CompleteAsync(ChunkedUpload upload, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Cancels the upload session, when the transfer discards what it uploaded. Does nothing by default, which leaves
    /// the session for the service to expire.
    /// </summary>
    protected virtual Task AbortAsync(ChunkedUpload upload, CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class Session(ChunkedUploadTarget target, ChunkedUpload upload, long acknowledged) : ITargetSession, IAcknowledgingSession
    {
        private long _acknowledged = acknowledged;

        public string? ResumeToken => upload.Token;

        public string? Location => upload.Location;

        public IReadOnlyDictionary<string, string> Metadata => new Dictionary<string, string>(upload.Metadata);

        public long Kept(long position) => Math.Max(0, Interlocked.Read(ref _acknowledged) - position);

        public async Task WritePartAsync(ByteRange range, Stream content, CancellationToken cancellationToken)
        {
            var offset = Interlocked.Read(ref _acknowledged);
            if (range.Start != offset)
            {
                throw new TransferException($"The upload is at {offset}, so it can't continue from {range.Start}", TransferErrorKind.Protocol, side: TransferSide.Target);
            }

            // One byte more than a chunk, to tell whether the content ends with this chunk
            var buffer = new byte[target.ChunkSize + 1];
            var filled = 0;
            var ended = false;
            var sent = false;
            while (true)
            {
                while (!ended && filled < buffer.Length)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        ended = true;
                    }
                    filled += read;
                }
                if (filled == 0 && (sent || offset > 0))
                {
                    // The last chunk went with the bytes before; nothing is left
                    return;
                }

                var count = Math.Min(filled, target.ChunkSize);
                var isLast = ended && filled <= target.ChunkSize;
                var chunk = new UploadChunk(offset, new ArraySegment<byte>(buffer, 0, count), isLast, isLast ? offset + count : upload.Source.Length);
                var reached = await target.SendChunkAsync(upload, chunk, cancellationToken).ConfigureAwait(false);
                var taken = reached - offset;
                if (taken < 0 || taken > count || (taken == 0 && count > 0))
                {
                    throw new TransferException($"The service says the upload is at {reached} after a chunk of {count} bytes at {offset}", TransferErrorKind.Protocol, side: TransferSide.Target);
                }

                sent = true;
                offset = reached;
                Interlocked.Exchange(ref _acknowledged, reached);
                // What the server didn't take goes again, at the front of the next chunk
                Buffer.BlockCopy(buffer, (int)taken, buffer, 0, filled - (int)taken);
                filled -= (int)taken;
                if (isLast && filled == 0)
                {
                    return;
                }
            }
        }

        public Task CompleteAsync(CancellationToken cancellationToken) => target.CompleteAsync(upload, cancellationToken);

        public Task AbortAsync(CancellationToken cancellationToken) => target.AbortAsync(upload, cancellationToken);

        public ValueTask DisposeAsync() => default;
    }
}
