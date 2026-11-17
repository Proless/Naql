using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Naql;

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
    /// An opaque value that changes when the content changes: an ETag, a last-modified time, a file's size and write time.
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
