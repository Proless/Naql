using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Naql;

/// <summary>
/// Resolves references with resolvers the caller chooses: <see cref="SourcesAsync"/> follows references from one
/// resolver to the next until only sources are left, and <see cref="TargetAsync"/> picks the resolver for a
/// destination. Nothing is registered anywhere; every call says which resolvers to use.
/// </summary>
public static class Resolve
{
    /// <summary>
    /// How many references <see cref="SourcesAsync"/> follows from the first one, by default.
    /// </summary>
    public const int DefaultMaxDepth = 8;

    /// <summary>
    /// Resolves <paramref name="reference"/> into sources with the first of <paramref name="resolvers"/> that
    /// handles it, and resolves every <see cref="ResolvedReference"/> the same way, placing what it gives under that
    /// reference's path, so only <see cref="ResolvedSource"/>s come out, as they are found.
    /// </summary>
    /// <param name="reference">What to resolve</param>
    /// <param name="resolvers">The resolvers to use, in order of preference</param>
    /// <param name="maxDepth">How many references to follow from the first one</param>
    /// <param name="cancellationToken">Stops resolving</param>
    /// <exception cref="TransferException">No resolver handles a reference
    /// (<see cref="TransferErrorKind.Unsupported"/>), references lead in a loop or too deep
    /// (<see cref="TransferErrorKind.Protocol"/>), or a resolver failed</exception>
    public static IAsyncEnumerable<ResolvedSource> SourcesAsync(Uri reference, IReadOnlyList<ISourceResolver> resolvers, int maxDepth = DefaultMaxDepth,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(resolvers);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
        return FollowAsync(reference, resolvers, "", maxDepth, Array.Empty<string>(), cancellationToken);
    }

    /// <summary>
    /// The target for <paramref name="relativePath"/> inside <paramref name="destination"/>, from the first of
    /// <paramref name="resolvers"/> that handles the destination.
    /// </summary>
    /// <exception cref="TransferException">No resolver handles the destination (<see cref="TransferErrorKind.Unsupported"/>),
    /// or the resolver failed</exception>
    public static ValueTask<ITransferTarget> TargetAsync(Uri destination, string relativePath, SourceInfo? info, IReadOnlyList<ITargetResolver> resolvers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(resolvers);
        RelativePath.Split(relativePath);

        var resolver = resolvers.FirstOrDefault(candidate => candidate.CanResolve(destination))
            ?? throw new TransferException($"No resolver handles the destination {Describe(destination)}", TransferErrorKind.Unsupported, side: TransferSide.Target);
        return resolver.ResolveAsync(destination, relativePath, info, cancellationToken);
    }

    private static async IAsyncEnumerable<ResolvedSource> FollowAsync(Uri reference, IReadOnlyList<ISourceResolver> resolvers, string path, int depthLeft,
        IReadOnlyList<string> visited, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var key = reference.AbsoluteUri;
        if (visited.Contains(key))
        {
            throw new TransferException($"The reference {Describe(reference)} leads back to itself", TransferErrorKind.Protocol, side: TransferSide.Source);
        }

        var resolver = resolvers.FirstOrDefault(candidate => candidate.CanResolve(reference))
            ?? throw new TransferException($"No resolver handles {Describe(reference)}", TransferErrorKind.Unsupported, side: TransferSide.Source);
        var trail = visited.Append(key).ToArray();

        await foreach (var resolved in resolver.ResolveAsync(reference, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var placed = RelativePath.Combine(path, resolved.Path);
            switch (resolved)
            {
                case ResolvedSource source:
                    yield return source.Path == placed ? source : new ResolvedSource(source.Source, placed, source.Info);
                    break;

                case ResolvedReference further:
                    if (depthLeft == 0)
                    {
                        throw new TransferException($"{Describe(reference)} leads to more than the allowed references", TransferErrorKind.Protocol, side: TransferSide.Source);
                    }
                    await foreach (var found in FollowAsync(further.Next, resolvers, placed, depthLeft - 1, trail, cancellationToken).ConfigureAwait(false))
                    {
                        yield return found;
                    }
                    break;

                default:
                    // Resolved can't be derived from outside this assembly, but a null item is still possible
                    throw new TransferException($"A resolver gave nothing usable for {Describe(reference)}", TransferErrorKind.Protocol, side: TransferSide.Source);
            }
        }
    }

    /// <summary>
    /// A reference for an error message, without user name, password or query, which can hold credentials.
    /// </summary>
    private static string Describe(Uri reference)
    {
        return reference.IsAbsoluteUri
            ? reference.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.SafeUnescaped)
            : reference.OriginalString;
    }
}
