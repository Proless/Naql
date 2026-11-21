namespace Naqel.Manager;

/// <summary>
/// What Naqel's own engine works with.
/// </summary>
public sealed class NaqelEngineOptions
{
    /// <summary>
    /// The resolvers that turn a job's reference into sources, in order. The engine handles every reference one of
    /// them handles.
    /// </summary>
    public IList<ISourceResolver> SourceResolvers { get; } = new List<ISourceResolver>();

    /// <summary>
    /// The resolvers that turn a job's destination into targets, in order.
    /// </summary>
    public IList<ITargetResolver> TargetResolvers { get; } = new List<ITargetResolver>();

    /// <summary>
    /// The settings of every transfer, such as connections and retries; a job's limits apply on top.
    /// </summary>
    public TransferOptions TransferOptions { get; set; } = new();
}
