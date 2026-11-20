using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Naql;

/// <summary>
/// Turns a reference into the sources it stands for: a link to a file gives one source, a shared folder, album or
/// playlist gives many, and a short link or a download page gives another reference for a different resolver.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A reference is any URI: <c>https:</c>, <c>file:</c>, <c>sftp:</c>, or a scheme of a package's own, such as
/// <c>gdrive:</c> or <c>dropbox:</c>.</item>
/// <item>Yield sources as they are found, so a large folder starts transferring before it's fully listed.</item>
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
    IAsyncEnumerable<ResolvedSource> ResolveAsync(Uri reference, CancellationToken cancellationToken);
}

/// <summary>
/// Turns a destination and a place within it into a target: <c>file:///backup/</c> and <c>photos/a.jpg</c> give a
/// file target for <c>/backup/photos/a.jpg</c>, <c>dropbox:/Backups</c> an upload into that folder.
/// </summary>
/// <remarks>
/// A target resolver must keep every path inside the destination and make each segment of
/// <c>relativePath</c> acceptable to its storage; <see cref="RelativePath.Split"/> checks and splits it.
/// Use <see cref="Resolve.TargetAsync"/> to pick one of several resolvers.
/// </remarks>
public interface ITargetResolver
{
    /// <summary>
    /// Whether this resolver handles <paramref name="destination"/>, from a quick look at it such as its scheme.
    /// </summary>
    bool CanResolve(Uri destination);

    /// <summary>
    /// Returns the target for <paramref name="relativePath"/> inside <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">Where content goes, such as a folder</param>
    /// <param name="relativePath">Where this content goes inside it, empty for the destination itself; see <see cref="RelativePath"/></param>
    /// <param name="info">What's known about the content, such as its name and length, if anything</param>
    /// <param name="cancellationToken">Stops resolving</param>
    ValueTask<ITransferTarget> ResolveAsync(Uri destination, string relativePath, SourceInfo? info, CancellationToken cancellationToken);
}

/// <summary>
/// What a reference resolved to: a source to transfer, or another reference to resolve further. Either way, it has
/// a place in the structure the reference stands for.
/// </summary>
public sealed class ResolvedSource
{
    private ResolvedSource(ITransferSource? source, Uri? next, string relativePath, SourceInfo? info)
    {
        RelativePath.Split(relativePath);
        Source = source;
        Next = next;
        Path = relativePath;
        Info = info;
    }

    /// <summary>
    /// A source to transfer.
    /// </summary>
    /// <param name="source">Transfers the content</param>
    /// <param name="relativePath">Where the content belongs, such as <c>album/02.mp3</c>; empty for a single item</param>
    /// <param name="info">What's already known about the content, if anything; the source may report more later</param>
    public static ResolvedSource ToSource(ITransferSource source, string relativePath = "", SourceInfo? info = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ResolvedSource(source, null, relativePath ?? throw new ArgumentNullException(nameof(relativePath)), info);
    }

    /// <summary>
    /// Another reference that a resolver, maybe a different one, resolves further, such as the page a short link
    /// leads to. What it resolves to belongs under <paramref name="relativePath"/>.
    /// </summary>
    public static ResolvedSource ToReference(Uri next, string relativePath = "")
    {
        ArgumentNullException.ThrowIfNull(next);
        return new ResolvedSource(null, next, relativePath ?? throw new ArgumentNullException(nameof(relativePath)), null);
    }

    /// <summary>
    /// The source to transfer, or <c>null</c> when this is a reference to resolve further.
    /// </summary>
    public ITransferSource? Source { get; }

    /// <summary>
    /// The reference to resolve further, or <c>null</c> when this is a source.
    /// </summary>
    public Uri? Next { get; }

    /// <summary>
    /// Where the content belongs in the structure, in the form <see cref="RelativePath"/> describes; empty for a
    /// single item.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// What was known about the content while resolving, such as its name and length, or <c>null</c>.
    /// </summary>
    public SourceInfo? Info { get; }
}
