using Naqel.Storage;

namespace Naqel.Plugins;

/// <summary>
/// Where a plugin registers what it brings, while Naqel loads it.
/// </summary>
public interface IPluginRegistry
{
    /// <summary>
    /// Adds a resolver for references, such as links, into sources. Resolvers are asked site ones first, then format
    /// ones, then protocol ones, and within a role in the order plugins and their calls added them.
    /// </summary>
    void AddSourceResolver(ISourceResolver resolver, ResolverRole role);

    /// <summary>
    /// Adds a resolver for destinations into targets, ordered as source resolvers are.
    /// </summary>
    void AddTargetResolver(ITargetResolver resolver, ResolverRole role);

    /// <summary>
    /// Adds a kind of storage that storage locations can use, such as a cloud drive; <paramref name="create"/> makes
    /// one from a location's settings, such as which account and folder.
    /// </summary>
    /// <param name="kind">The kind's stable name, such as <c>dropbox</c></param>
    /// <param name="title">The kind's name, to show</param>
    /// <param name="settings">What a location of this kind needs to be set up</param>
    /// <param name="create">Makes a storage from a location's values for <paramref name="settings"/></param>
    void AddStorageKind(string kind, string title, IReadOnlyList<SettingDefinition> settings, Func<IReadOnlyDictionary<string, string>, IStorage> create);

    /// <summary>
    /// Adds an engine, for protocols such as BitTorrent. Engines are offered new jobs before Naqel's own.
    /// </summary>
    void AddEngine(ITransferEngine engine);

    /// <summary>
    /// Adds an observer of the jobs' events.
    /// </summary>
    void AddObserver(IJobObserver observer);

    /// <summary>
    /// Adds a step that runs when a job's content is complete.
    /// </summary>
    void AddCompletionStep(ICompletionStep completionStep);

    /// <summary>
    /// Adds a way to sign in to a service, such as a cloud drive.
    /// </summary>
    void AddAccountProvider(IAccountProvider provider);

    /// <summary>
    /// Describes the plugin's settings, which both UIs show and the plugin reads through <see cref="IPluginContext.Settings"/>.
    /// </summary>
    void AddSettings(IReadOnlyList<SettingDefinition> settings);
}
