using System.Text.Json;
using Naql.Http;

namespace Naql.Samples.Dropbox;

/// <summary>
/// An upload to a Dropbox path through an upload session: started, appended to chunk by chunk, and finished into the
/// file. <see cref="ChunkedUploadTarget"/> does the rest: it sends the chunks in order, sends again what Dropbox
/// didn't take, and lets a failed or later transfer continue the session from what Dropbox has.
/// </summary>
public sealed class DropboxTarget : ChunkedUploadTarget
{
    /// <summary>
    /// The default chunk size, 8 MiB. Dropbox takes up to 150 MiB per call.
    /// </summary>
    public const int DefaultChunkSize = 8 * 1024 * 1024;

    private readonly DropboxClient _client;

    /// <summary>
    /// An upload to <paramref name="path"/>.
    /// </summary>
    /// <param name="client">The account to upload to</param>
    /// <param name="path">The file's Dropbox path, such as <c>/Backups/a.zip</c></param>
    /// <param name="overwrite">Whether to replace a file already there; otherwise the upload fails as
    /// <see cref="TransferErrorKind.AlreadyExists"/></param>
    /// <param name="chunkSize">The bytes in every chunk but the last</param>
    public DropboxTarget(DropboxClient client, string path, bool overwrite = true, int chunkSize = DefaultChunkSize)
        : base((client ?? throw new ArgumentNullException(nameof(client))).Api, chunkSize)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _client = client;
        Path = path;
        Overwrite = overwrite;
    }

    /// <summary>
    /// The file's Dropbox path.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Whether a file already there is replaced.
    /// </summary>
    public bool Overwrite { get; }

    /// <inheritdoc />
    protected override async Task StartAsync(ChunkedUpload upload, CancellationToken cancellationToken)
    {
        var reply = await _client.UploadAsync("files/upload_session/start", new { close = false }, default, cancellationToken).ConfigureAwait(false);
        upload.Token = reply.Error is { } error ? throw error : reply.Body.GetProperty("session_id").GetString();
    }

    /// <inheritdoc />
    protected override async Task<long> SendChunkAsync(ChunkedUpload upload, UploadChunk chunk, CancellationToken cancellationToken)
    {
        var reached = await AppendAsync(upload, chunk.Offset, chunk.Content, cancellationToken).ConfigureAwait(false);
        // Dropbox may already have this chunk, when the answer to an earlier send of it was lost
        return reached >= chunk.Offset && reached <= chunk.Offset + chunk.Count
            ? reached
            : throw new TransferException($"Dropbox says the upload is at {reached}, but this chunk covers {chunk.Offset} to {chunk.Offset + chunk.Count}",
                TransferErrorKind.Protocol, side: TransferSide.Target);
    }

    /// <summary>
    /// Dropbox can't be asked where a session stands, but it says so when a chunk arrives at the wrong offset: an
    /// empty chunk at 0 succeeds for a session with nothing yet, and otherwise names the right offset.
    /// </summary>
    protected override Task<long> QueryAsync(ChunkedUpload upload, CancellationToken cancellationToken)
        => AppendAsync(upload, 0, default, cancellationToken);

    /// <inheritdoc />
    protected override async Task CompleteAsync(ChunkedUpload upload, CancellationToken cancellationToken)
    {
        var arguments = new
        {
            cursor = new { session_id = upload.Token, offset = upload.Acknowledged },
            commit = new { path = Path, mode = Overwrite ? "overwrite" : "add", autorename = false, mute = true }
        };
        var reply = await _client.UploadAsync("files/upload_session/finish", arguments, default, cancellationToken).ConfigureAwait(false);
        if (reply.Error is { } error)
        {
            throw error;
        }

        var file = DropboxEntry.Parse(reply.Body);
        upload.Location = DropboxPath.ToReference(file.Path).AbsoluteUri;
        upload.Metadata["id"] = file.Id ?? "";
        upload.Metadata["rev"] = file.Rev ?? "";
    }

    // Sends bytes at an offset, and returns where the session is after them
    private async Task<long> AppendAsync(ChunkedUpload upload, long offset, ArraySegment<byte> content, CancellationToken cancellationToken)
    {
        var arguments = new { cursor = new { session_id = upload.Token, offset }, close = false };
        var reply = await _client.UploadAsync("files/upload_session/append_v2", arguments, content, cancellationToken).ConfigureAwait(false);
        if (reply.Error == null)
        {
            return offset + content.Count;
        }
        if (reply.ErrorSummary?.StartsWith("incorrect_offset", StringComparison.Ordinal) == true &&
            reply.Body.TryGetProperty("error", out var error) && error.TryGetProperty("correct_offset", out var correct) &&
            correct.ValueKind == JsonValueKind.Number)
        {
            return correct.GetInt64();
        }
        throw reply.Error;
    }
}
