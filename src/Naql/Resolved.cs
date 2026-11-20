namespace Naql;

/// <summary>
/// What a reference resolved to: a <see cref="ResolvedSource"/> to transfer, or a <see cref="ResolvedReference"/> to
/// resolve further. Either way, it has a place in the structure the reference stands for.
/// </summary>
public abstract class Resolved
{
    // Only the two kinds above exist, so callers can tell them apart with a type test
    private protected Resolved(string path)
    {
        RelativePath.Split(path);
        Path = path;
    }

    /// <summary>
    /// Where it belongs in the structure, such as <c>album/02.mp3</c>, in the form <see cref="RelativePath"/>
    /// describes; empty for a single item.
    /// </summary>
    public string Path { get; }
}
