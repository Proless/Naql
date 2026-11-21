using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Naqel.Http;

/// <summary>
/// Resolves <c>http:</c> and <c>https:</c> destinations into <c>PUT</c> uploads, the way WebDAV and similar servers
/// take files: a folder URL that ends with <c>/</c> and a relative path such as <c>photos/a b.jpg</c> give a
/// <c>PUT</c> to <c>…/photos/a%20b.jpg</c>, and a destination without a relative path is the URL to upload to.
/// </summary>
/// <remarks>
/// Each segment of the relative path is escaped, so it stays one segment inside the folder. Whether a server creates
/// missing folders is up to the server; WebDAV servers expect them to exist.
/// </remarks>
public sealed class HttpTargetResolver : ITargetResolver
{
    /// <summary>
    /// A resolver whose targets use <paramref name="transport"/>, or <see cref="HttpTransport.Default"/>.
    /// </summary>
    public HttpTargetResolver(HttpTransport? transport = null)
    {
        Transport = transport ?? HttpTransport.Default;
    }

    /// <summary>
    /// The client and request settings for the targets.
    /// </summary>
    public HttpTransport Transport { get; }

    /// <inheritdoc />
    public bool CanResolve(Uri destination) =>
        destination is { IsAbsoluteUri: true } && (destination.Scheme == Uri.UriSchemeHttp || destination.Scheme == Uri.UriSchemeHttps);

    /// <inheritdoc />
    public ValueTask<ITransferTarget> ResolveAsync(Uri destination, string relativePath, SourceInfo? info, ResolveContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var segments = RelativePath.Split(relativePath);
        if (segments.Count == 0)
        {
            return new ValueTask<ITransferTarget>(Transport.Put(destination));
        }
        if (!destination.AbsolutePath.EndsWith('/'))
        {
            throw new TransferException($"'{destination.GetLeftPart(UriPartial.Path)}' isn't a folder, so it can't hold '{relativePath}'; a folder URL ends with '/'",
                TransferErrorKind.Unsupported, side: TransferSide.Target);
        }

        var escaped = string.Join("/", segments.Select(Uri.EscapeDataString));
        return new ValueTask<ITransferTarget>(Transport.Put(new Uri(destination, escaped)));
    }
}
