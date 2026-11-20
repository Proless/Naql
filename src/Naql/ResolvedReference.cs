using System;

namespace Naql;

/// <summary>
/// Another reference a reference resolved to, for a resolver, maybe a different one, to resolve further: the page a
/// short link leads to, the file a download page offers. What it resolves to belongs under its <see cref="Resolved.Path"/>.
/// </summary>
public sealed class ResolvedReference : Resolved
{
    /// <summary>
    /// A reference to resolve further.
    /// </summary>
    /// <param name="next">The reference to resolve</param>
    /// <param name="path">Where what it resolves to belongs, such as <c>art</c> for a subfolder; empty for the same place</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> isn't a valid <see cref="RelativePath"/></exception>
    public ResolvedReference(Uri next, string path = "")
        : base(path ?? throw new ArgumentNullException(nameof(path)))
    {
        Next = next ?? throw new ArgumentNullException(nameof(next));
    }

    /// <summary>
    /// The reference to resolve.
    /// </summary>
    public Uri Next { get; }
}
