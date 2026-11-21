using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Naqel;

/// <summary>
/// Describes the content of a source. It always describes the whole content, never just the range that was read.
/// </summary>
public sealed record SourceInfo
{
    /// <summary>
    /// The length of the whole content, or <c>null</c> when it's unknown (chunked HTTP, a live stream).
    /// </summary>
    public long? Length
    {
        get;
        init
        {
            if (value is long length)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(length, nameof(Length));
            }
            field = value;
        }
    }

    /// <summary>
    /// Whether <see cref="ITransferSource.OpenReadAsync"/> can start anywhere but at the first byte.
    /// </summary>
    public bool SupportsRanges { get; init; }

    /// <summary>
    /// Whether the content can be read again after a failure. A one-time stream can't, so it's never retried.
    /// </summary>
    public bool IsRepeatable { get; init; } = true;

    /// <summary>
    /// The most connections that may read the content at once, such as 1 for a file host's free account, or
    /// <c>null</c> for no limit. The engine uses the smaller of this and <see cref="TransferOptions.MaxConnections"/>.
    /// </summary>
    public int? MaxConnections
    {
        get;
        init
        {
            if (value is int connections)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(connections, nameof(MaxConnections));
            }
            field = value;
        }
    }

    /// <summary>
    /// What the content is, independent of its version: the requested URL, a file's full path. A checkpoint only
    /// continues a transfer whose source reports the same identity, so it can't be applied to other content that
    /// happens to have the same version and length. <c>null</c> when the source can't tell.
    /// </summary>
    /// <remarks>
    /// Checkpoints keep a fingerprint of it, not the value, so a URL with credentials in it isn't written to disk.
    /// </remarks>
    public string? Identity { get; init; }

    /// <summary>
    /// An opaque value that changes when the content changes: an ETag, a last-modified time, a file's size and write
    /// time. Without one, the engine reads the content in one piece and makes no checkpoint, because it can't tell
    /// whether separate reads came from the same content.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// A suggested file name for the content.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// The media type of the content, such as <c>application/zip</c>.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// When the content was last changed.
    /// </summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// The hash the source reports for the content, such as HTTP's <c>Repr-Digest</c>. The engine verifies it when
    /// that costs nothing extra; <see cref="TransferOptions.ExpectedChecksum"/> asks for a verification in any case.
    /// </summary>
    public Checksum? Checksum { get; init; }

    /// <summary>
    /// Other properties of the content, passed on to targets that can store them.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value, nameof(Metadata));
            field = value;
        }
    } = ReadOnlyDictionary<string, string>.Empty;
}
