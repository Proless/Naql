using System;
using System.Collections.Generic;

namespace Naql.Http;

/// <summary>
/// One upload session of a <see cref="ChunkedUploadTarget"/>: what the steps share about it.
/// </summary>
public sealed class ChunkedUpload
{
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
