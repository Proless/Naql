namespace Naqel.Files;

/// <summary>
/// Options for one resolution by <see cref="FileSourceResolver"/> and <see cref="FileTargetResolver"/>, passed with
/// <see cref="ResolveContext.With{T}"/>. A value left <c>null</c> keeps the resolver's own setting.
/// </summary>
public sealed record FileResolveOptions
{
    /// <summary>
    /// Whether a folder includes the files of its subfolders, instead of <see cref="FileSourceResolver.Recursive"/>.
    /// </summary>
    public bool? Recursive { get; init; }

    /// <summary>
    /// What a target does when its file already exists, instead of <see cref="FileTargetResolver.Conflict"/>.
    /// </summary>
    public FileConflict? Conflict { get; init; }
}
