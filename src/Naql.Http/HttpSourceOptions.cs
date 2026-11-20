using System;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Http;

/// <summary>
/// How an <see cref="HttpSource"/> describes and finds its content, for packages whose services hand out links of
/// their own: signed URLs that expire, a new link on every request, names that only the service's API knows.
/// </summary>
public sealed record HttpSourceOptions
{
    /// <summary>
    /// What the content is, for <see cref="SourceInfo.Identity"/>, such as <c>gdrive:1AbC</c>, instead of the URL. A
    /// checkpoint only continues a transfer whose source has the same identity, so a stable one lets a transfer resume
    /// with a link that changed.
    /// </summary>
    public string? Identity { get; init; }

    /// <summary>
    /// The suggested file name, for <see cref="SourceInfo.Name"/>, instead of the one from <c>Content-Disposition</c>
    /// or the URL.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets a new URL for the content when the current one expired, such as asking a service's API for a new signed
    /// link. When a request is answered with 401, 403 or 410, the source calls it and sends the request again to the
    /// new URL, once for each time the URL expires; parts that run at the same time share one refresh. Reads keep
    /// checking the content's version, so a new URL to changed content still fails as
    /// <see cref="TransferErrorKind.SourceChanged"/>.
    /// </summary>
    public Func<CancellationToken, Task<Uri>>? Refresh { get; init; }
}
