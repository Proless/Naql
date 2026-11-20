using System;
using System.Collections.Generic;
using System.Threading;

namespace Naql;

/// <summary>
/// Turns a reference into the sources it stands for: a link to a file gives one source, a shared folder, album or
/// playlist gives many, and a short link or a download page gives another reference for a different resolver.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A reference is any URI: <c>https:</c>, <c>file:</c>, <c>sftp:</c>, or a scheme of a package's own, such as
/// <c>gdrive:</c> or <c>dropbox:</c>.</item>
/// <item>Yield a <see cref="ResolvedSource"/> for content to transfer and a <see cref="ResolvedReference"/> for a
/// reference to resolve further, as they are found, so a large folder starts transferring before it's fully listed.</item>
/// <item>Fail with a <see cref="TransferException"/> whose <see cref="TransferException.Kind"/> says why, such as
/// <see cref="TransferErrorKind.NotFound"/> or <see cref="TransferErrorKind.Unauthorized"/>.</item>
/// <item>Anything that needs the user, such as signing in, belongs to the caller; a resolver gets what it needs, such
/// as credentials, when it's created.</item>
/// </list>
/// Use <see cref="Resolve.SourcesAsync"/> to resolve with several resolvers and follow references between them.
/// </remarks>
public interface ISourceResolver
{
    /// <summary>
    /// Whether this resolver handles <paramref name="reference"/>, from a quick look at it such as its scheme or host.
    /// </summary>
    bool CanResolve(Uri reference);

    /// <summary>
    /// Resolves <paramref name="reference"/> into sources, or into further references.
    /// </summary>
    IAsyncEnumerable<Resolved> ResolveAsync(Uri reference, CancellationToken cancellationToken);
}
