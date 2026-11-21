using Naqel.Plugins;
using Naqel.Storage;

namespace Naqel.Manager;

/// <summary>
/// What the host built in and the plugins brought, in the order Naqel uses them: resolvers by role, site ones first,
/// then format ones, then protocol ones, and within a role in the order they were registered.
/// </summary>
public sealed class PluginSet
{
    private readonly List<(ResolverRole Role, int Order, ISourceResolver Resolver)> _sources = [];
    private readonly List<(ResolverRole Role, int Order, ITargetResolver Resolver)> _targets = [];
    private int _order;

    internal PluginSet()
    {
    }

    /// <summary>
    /// The plugins that loaded, and the host's own built-in registrations, in the order they registered.
    /// </summary>
    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    /// <summary>
    /// The installed plugins that didn't load, and why.
    /// </summary>
    public IReadOnlyList<PluginFailure> Failures => _failures;

    /// <summary>
    /// The source resolvers, in the order to ask them.
    /// </summary>
    public IReadOnlyList<ISourceResolver> SourceResolvers =>
        [.. _sources.OrderBy(entry => entry.Role).ThenBy(entry => entry.Order).Select(entry => entry.Resolver)];

    /// <summary>
    /// The target resolvers, in the order to ask them.
    /// </summary>
    public IReadOnlyList<ITargetResolver> TargetResolvers =>
        [.. _targets.OrderBy(entry => entry.Role).ThenBy(entry => entry.Order).Select(entry => entry.Resolver)];

    /// <summary>The engines plugins brought, which go before Naqel's own.</summary>
    public IReadOnlyList<ITransferEngine> Engines => _engines;

    /// <summary>The observers of the jobs' events.</summary>
    public IReadOnlyList<IJobObserver> Observers => _observers;

    /// <summary>The completion steps, in order.</summary>
    public IReadOnlyList<ICompletionStep> CompletionSteps => _steps;

    /// <summary>The ways to sign in to services.</summary>
    public IReadOnlyList<IAccountProvider> AccountProviders => _providers;

    /// <summary>The kinds of storage locations can use.</summary>
    public IReadOnlyList<StorageKind> StorageKinds => _kinds;

    private readonly List<LoadedPlugin> _plugins = [];
    private readonly List<PluginFailure> _failures = [];
    private readonly List<ITransferEngine> _engines = [];
    private readonly List<IJobObserver> _observers = [];
    private readonly List<ICompletionStep> _steps = [];
    private readonly List<IAccountProvider> _providers = [];
    private readonly List<StorageKind> _kinds = [];

    /// <summary>
    /// Gives Naqel's own engine the resolvers, in order.
    /// </summary>
    public void ApplyTo(NaqelEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var resolver in SourceResolvers)
        {
            options.SourceResolvers.Add(resolver);
        }
        foreach (var resolver in TargetResolvers)
        {
            options.TargetResolvers.Add(resolver);
        }
    }

    /// <summary>
    /// Gives the manager the plugins' engines, observers, completion steps and account providers. The engines go before
    /// the ones the options have, such as Naqel's own, so they're offered new jobs first.
    /// </summary>
    public void ApplyTo(JobManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        for (var i = _engines.Count - 1; i >= 0; i--)
        {
            options.Engines.Insert(0, _engines[i]);
        }
        foreach (var observer in _observers)
        {
            options.Observers.Add(observer);
        }
        foreach (var step in _steps)
        {
            options.CompletionSteps.Add(step);
        }
        foreach (var provider in _providers)
        {
            options.AccountProviders.Add(provider);
        }
    }

    internal void Add(LoadedPlugin plugin) => _plugins.Add(plugin);

    internal void Fail(PluginFailure failure) => _failures.Add(failure);

    /// <summary>
    /// Collects one plugin's registrations, which join the set only if it registers without failing.
    /// </summary>
    internal sealed class Registry(PluginSet set, PluginSettings pluginSettings) : IPluginRegistry
    {
        private readonly List<Action> _commits = [];

        public void AddSourceResolver(ISourceResolver resolver, ResolverRole role)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            _commits.Add(() => set._sources.Add((role, set._order++, resolver)));
        }

        public void AddTargetResolver(ITargetResolver resolver, ResolverRole role)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            _commits.Add(() => set._targets.Add((role, set._order++, resolver)));
        }

        public void AddStorageKind(string kind, string title, IReadOnlyList<SettingDefinition> settings, Func<IReadOnlyDictionary<string, string>, IStorage> create)
        {
            ArgumentException.ThrowIfNullOrEmpty(kind);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(create);
            _commits.Add(() => set._kinds.Add(new StorageKind(kind, title, settings, create)));
        }

        public void AddEngine(ITransferEngine engine)
        {
            ArgumentNullException.ThrowIfNull(engine);
            _commits.Add(() => set._engines.Add(engine));
        }

        public void AddObserver(IJobObserver observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            _commits.Add(() => set._observers.Add(observer));
        }

        public void AddCompletionStep(ICompletionStep completionStep)
        {
            ArgumentNullException.ThrowIfNull(completionStep);
            _commits.Add(() => set._steps.Add(completionStep));
        }

        public void AddAccountProvider(IAccountProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            _commits.Add(() => set._providers.Add(provider));
        }

        public void AddSettings(IReadOnlyList<SettingDefinition> settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            pluginSettings.Define(settings);
        }

        public void Commit()
        {
            foreach (var commit in _commits)
            {
                commit();
            }
        }
    }
}
