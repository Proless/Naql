using System;
using System.Collections.Generic;
using System.Threading;

namespace Naqel.Http;

/// <summary>
/// One upload session of a <see cref="ChunkedUploadTarget"/>: what the steps share about it.
/// </summary>
public sealed class ChunkedUpload
{
    private long _acknowledged;

    internal ChunkedUpload(SourceInfo source, string? token)
    {
        Source = source;
        Token = token;
    }

    /// <summary>
    /// What's being uploaded: its length, if known, name and media type.
    /// </summary>
    public SourceInfo Source { get; }

    /// <summary>
    /// What identifies the session with the service, such as its upload URL or session id. The start step sets it;
    /// it's the resume token, so a later run continues the session from it alone.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// How many bytes from the start of the content the service has acknowledged: what the last chunk reached, or
    /// what <see cref="ChunkedUploadTarget"/>'s query found when a later run reopened the session. When the finish
    /// step runs, it's the content's whole length, which services such as Dropbox need to finish an upload, also for
    /// content whose length wasn't known up front.
    /// </summary>
    public long Acknowledged
    {
        get => Interlocked.Read(ref _acknowledged);
        internal set => Interlocked.Exchange(ref _acknowledged, value);
    }

    /// <summary>
    /// Where the content is once the upload completed, such as the file's URL, for
    /// <see cref="TransferResult.Location"/>. Any step can set it, such as from the answer to the last chunk.
    /// </summary>
    public string? Location { get; set; }

    /// <summary>
    /// What the service created, such as a file id, for <see cref="TransferResult.TargetMetadata"/>. Any step can add
    /// to it.
    /// </summary>
    public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>();
}

/// <summary>
/// A chunk for a <see cref="ChunkedUploadTarget"/> to send.
/// </summary>
public sealed class UploadChunk
{
    internal UploadChunk(long offset, ArraySegment<byte> content, bool isLast, long? totalLength)
    {
        Offset = offset;
        Content = content;
        IsLast = isLast;
        TotalLength = totalLength;
    }

    /// <summary>
    /// Where the chunk starts in the content.
    /// </summary>
    public long Offset { get; }

    /// <summary>
    /// The bytes of the chunk; valid only until the send step returns.
    /// </summary>
    public ArraySegment<byte> Content { get; }

    /// <summary>
    /// The number of bytes in the chunk.
    /// </summary>
    public int Count => Content.Count;

    /// <summary>
    /// Whether this is the last chunk, which many services need to know, such as to finish the upload.
    /// </summary>
    public bool IsLast { get; }

    /// <summary>
    /// The length of the whole content: known from the start when the source reported it, and always known for the
    /// last chunk.
    /// </summary>
    public long? TotalLength { get; }
}
