using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Naqel.Http;

/// <summary>
/// Resolves <c>http:</c> and <c>https:</c> links into <see cref="HttpSource"/>s: the plain resolver for direct links,
/// and the one to put last, after resolvers for particular services that recognize their own links.
/// </summary>
public sealed class HttpSourceResolver : ISourceResolver
{
    /// <summary>
    /// A resolver whose sources use <paramref name="transport"/>, or <see cref="HttpTransport.Default"/>.
    /// </summary>
    /// <param name="transport">The client and request settings for the sources</param>
    /// <param name="describe">Whether to ask the server about the content while resolving, so its name and length
    /// are known before transferring; that costs a request per link</param>
    public HttpSourceResolver(HttpTransport? transport = null, bool describe = true)
    {
        Transport = transport ?? HttpTransport.Default;
        Describe = describe;
    }

    /// <summary>
    /// The client and request settings for the sources.
    /// </summary>
    public HttpTransport Transport { get; }

    /// <summary>
    /// Whether resolving asks the server about the content.
    /// </summary>
    public bool Describe { get; }

    /// <inheritdoc />
    public bool CanResolve(Uri reference) =>
        reference is { IsAbsoluteUri: true } && (reference.Scheme == Uri.UriSchemeHttp || reference.Scheme == Uri.UriSchemeHttps);

    /// <inheritdoc />
    public async IAsyncEnumerable<Resolved> ResolveAsync(Uri reference, ResolveContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var source = Transport.Get(reference);
        var info = Describe ? await source.GetInfoAsync(cancellationToken).ConfigureAwait(false) : null;
        yield return new ResolvedSource(source, "", info);
    }
}
