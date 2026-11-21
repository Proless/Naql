using System;
using System.Collections.Generic;

namespace Naqel;

/// <summary>
/// The choices that belong to one resolution rather than to a resolver: options a resolver looks for, such as which
/// quality of a video or which account, and which of the found sources to keep.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Options are typed: a package defines its own options class, and a resolver reads it with
/// <see cref="Get{T}"/>. A resolver ignores options it doesn't know, so one context can carry options for every
/// resolver a reference passes through.</item>
/// <item>A context never changes; <see cref="With{T}"/> returns a copy.</item>
/// </list>
/// </remarks>
public sealed class ResolveContext
{
    private readonly IReadOnlyDictionary<Type, object> _options;

    /// <summary>
    /// No options and no filter.
    /// </summary>
    public static ResolveContext Default { get; } = new();

    /// <summary>
    /// A context with no options; set them with <see cref="With{T}"/>.
    /// </summary>
    /// <param name="filter">Which sources to keep, see <see cref="Filter"/></param>
    public ResolveContext(Func<ResolvedSource, bool>? filter = null)
        : this(new Dictionary<Type, object>(), filter)
    {
    }

    private ResolveContext(IReadOnlyDictionary<Type, object> options, Func<ResolvedSource, bool>? filter)
    {
        _options = options;
        Filter = filter;
    }

    /// <summary>
    /// Which sources to keep, given their place in the whole structure, or <c>null</c> to keep all.
    /// <see cref="Resolve.SourcesAsync"/> applies it to each source once its path is final, such as
    /// <c>album/02.mp3</c>, so it can choose by path, name, length or type.
    /// </summary>
    public Func<ResolvedSource, bool>? Filter { get; }

    /// <summary>
    /// The options of type <typeparamref name="T"/>, or <c>null</c> when there are none.
    /// </summary>
    public T? Get<T>() where T : class => _options.TryGetValue(typeof(T), out var options) ? (T)options : null;

    /// <summary>
    /// A copy of this context with <paramref name="options"/> as its options of type <typeparamref name="T"/>,
    /// replacing any it had.
    /// </summary>
    public ResolveContext With<T>(T options) where T : class
    {
        ArgumentNullException.ThrowIfNull(options);
        var copy = new Dictionary<Type, object>();
        foreach (var pair in _options)
        {
            copy[pair.Key] = pair.Value;
        }
        copy[typeof(T)] = options;
        return new ResolveContext(copy, Filter);
    }

    /// <summary>
    /// A copy of this context that keeps only the sources <paramref name="filter"/> accepts, or all with <c>null</c>.
    /// </summary>
    public ResolveContext WithFilter(Func<ResolvedSource, bool>? filter) => new(_options, filter);
}
