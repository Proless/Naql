using System;
using System.Threading;
using System.Threading.Tasks;

namespace Naql;

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
    /// <param name="context">The choices for this resolution, such as options of the resolver's own type</param>
    /// <param name="cancellationToken">Stops resolving</param>
    ValueTask<ITransferTarget> ResolveAsync(Uri destination, string relativePath, SourceInfo? info, ResolveContext context, CancellationToken cancellationToken);
}
