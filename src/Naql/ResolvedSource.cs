using System;

namespace Naql;

/// <summary>
/// A source a reference resolved to, with its place in the structure the reference stands for.
/// </summary>
public sealed class ResolvedSource : Resolved
{
    /// <summary>
    /// A source to transfer.
    /// </summary>
    /// <param name="source">Transfers the content</param>
    /// <param name="path">Where the content belongs, such as <c>album/02.mp3</c>; empty for a single item</param>
    /// <param name="info">What's already known about the content, if anything, such as the name and length a folder
    /// listing gave; the source may report more once it's read</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> isn't a valid <see cref="RelativePath"/></exception>
    public ResolvedSource(ITransferSource source, string path = "", SourceInfo? info = null)
        : base(path ?? throw new ArgumentNullException(nameof(path)))
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Info = info;
    }

    /// <summary>
    /// The source to transfer.
    /// </summary>
    public ITransferSource Source { get; }

    /// <summary>
    /// What was known about the content while resolving, such as its name and length, or <c>null</c>.
    /// </summary>
    public SourceInfo? Info { get; }
}
