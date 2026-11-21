using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Naqel;

/// <summary>
/// Where a transfer reads its content from: an HTTP URL, a file, a stream.
/// </summary>
/// <remarks>
/// A source only reads bytes. Retries, timeouts, throttling, parallel parts and progress are done by the engine.
/// Errors should be thrown as <see cref="TransferException"/> with the matching <see cref="TransferErrorKind"/>;
/// the engine treats <see cref="IOException"/> and <see cref="TimeoutException"/> as transient network errors.
/// </remarks>
public interface ITransferSource
{
    /// <summary>
    /// Describes the content without transferring it.
    /// </summary>
    /// <remarks>
    /// The engine only calls this when it needs to plan parallel parts. A single-connection transfer reads the info from
    /// <see cref="SourceRead.Info"/> instead, so it costs one request.
    /// </remarks>
    Task<SourceInfo> GetInfoAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens the content for reading a range.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Return once the content is ready to read, for example when the response headers have arrived.</item>
    /// <item>When <see cref="SourceInfo.SupportsRanges"/> is <c>false</c>, the engine only asks for <see cref="ByteRange.All"/>.</item>
    /// <item>When <see cref="ReadRequest.IfVersion"/> is set and the content changed, throw a <see cref="TransferException"/>
    /// with <see cref="TransferErrorKind.SourceChanged"/> instead of returning different bytes.</item>
    /// </list>
    /// </remarks>
    Task<SourceRead> OpenReadAsync(ReadRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// What <see cref="ITransferSource.OpenReadAsync"/> should open.
/// </summary>
public sealed class ReadRequest
{
    /// <summary>
    /// Creates a request.
    /// </summary>
    public ReadRequest(ByteRange range, string? ifVersion = null)
    {
        Range = range;
        IfVersion = ifVersion;
    }

    /// <summary>
    /// The bytes to read.
    /// </summary>
    public ByteRange Range { get; }

    /// <summary>
    /// When set, the read must fail with <see cref="TransferErrorKind.SourceChanged"/> if the content is no longer this
    /// <see cref="SourceInfo.Version"/>.
    /// </summary>
    public string? IfVersion { get; }
}

/// <summary>
/// The content of an opened read, and what the source learned about it while opening it.
/// </summary>
public sealed class SourceRead : IDisposable
{
    /// <summary>
    /// Creates the result of a read.
    /// </summary>
    public SourceRead(Stream content, SourceInfo info)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Info = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>
    /// The bytes of the requested range.
    /// </summary>
    public Stream Content { get; }

    /// <summary>
    /// Describes the whole content, not just the requested range.
    /// </summary>
    public SourceInfo Info { get; }

    /// <summary>
    /// Disposes <see cref="Content"/>.
    /// </summary>
    public void Dispose() => Content.Dispose();
}
