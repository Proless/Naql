using Naqel.Plugins;
using Naqel.Storage;

namespace Naqel.Manager;

/// <summary>
/// A kind of storage a plugin brings, such as a cloud drive, which storage locations can use.
/// </summary>
/// <param name="Kind">The kind's stable name, such as <c>dropbox</c></param>
/// <param name="Title">Its name, to show</param>
/// <param name="Settings">What a location of this kind needs to be set up</param>
/// <param name="Create">Makes a storage from a location's values for <paramref name="Settings"/></param>
public sealed record StorageKind(string Kind, string Title, IReadOnlyList<SettingDefinition> Settings, Func<IReadOnlyDictionary<string, string>, IStorage> Create);
