using Naqel.Storage;

namespace Naqel.Core.Tests.Fakes;

/// <summary>
/// A storage in memory, shaped like a cloud drive when asked: items with ids, paths that ignore case, and files that
/// share a path, as Google Drive allows. Folders exist while files are in them.
/// </summary>
public sealed class FakeStorage(StorageCapabilities? capabilities = null) : IStorage, IStorageDelete, IStorageMove, IStorageLookup
{
    private readonly object _lock = new();
    private readonly List<(string Path, string Id, byte[] Content)> _files = [];
    private int _ids;

    public StorageCapabilities Capabilities { get; } = capabilities ?? new StorageCapabilities();

    private StringComparison Comparison => Capabilities.IgnoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string Add(string path, byte[] content)
    {
        lock (_lock)
        {
            var id = $"id{++_ids}";
            _files.Add((path, id, content));
            return id;
        }
    }

    public byte[]? Read(string path)
    {
        lock (_lock)
        {
            return _files.FirstOrDefault(file => string.Equals(file.Path, path, Comparison)).Content;
        }
    }

    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (_lock)
            {
                return [.. _files.Select(file => file.Path).OrderBy(path => path, StringComparer.Ordinal)];
            }
        }
    }

    public Task<StorageItem?> GetAsync(string path, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var file = _files.FirstOrDefault(file => string.Equals(file.Path, path, Comparison));
            if (file.Path != null)
            {
                return Task.FromResult<StorageItem?>(Describe(file));
            }
            if (path.Length == 0)
            {
                return Task.FromResult<StorageItem?>(new StorageItem("", "", IsFolder: true));
            }
            var inside = _files.FirstOrDefault(file => file.Path.StartsWith(path + "/", Comparison));
            return Task.FromResult(inside.Path == null ? null : Folder(inside.Path[..path.Length]));
        }
    }

    public async IAsyncEnumerable<StorageItem> ListAsync(string folder, bool recursive, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var found = await GetAsync(folder, cancellationToken);
        if (found is not { IsFolder: true })
        {
            throw new TransferException($"There's no folder {folder}", TransferErrorKind.NotFound, side: TransferSide.Source);
        }
        List<StorageItem> items;
        lock (_lock)
        {
            var prefix = found.Path.Length == 0 ? "" : found.Path + "/";
            var below = _files.Where(file => file.Path.StartsWith(prefix, Comparison)).ToList();
            var folders = new SortedSet<string>(StringComparer.Ordinal);
            items = [];
            foreach (var file in below)
            {
                var rest = file.Path[prefix.Length..].Split('/');
                for (var depth = 1; depth < rest.Length && (recursive || depth == 1); depth++)
                {
                    folders.Add(prefix + string.Join("/", rest.Take(depth)));
                }
                if (recursive || rest.Length == 1)
                {
                    items.Add(Describe(file));
                }
            }
            items.InsertRange(0, folders.Select(Folder));
        }
        foreach (var item in items)
        {
            yield return item;
        }
    }

    public ITransferSource OpenRead(StorageItem file)
    {
        lock (_lock)
        {
            var found = _files.Single(entry => entry.Id == file.Id);
            return new MemorySource(found.Content) { Identity = "fake:" + found.Id };
        }
    }

    public ITransferTarget OpenWrite(string path, WriteMode mode, SourceInfo? info)
    {
        MemoryTarget target = null!;
        target = new MemoryTarget
        {
            OnOpen = (_, _) => mode == WriteMode.Fail && Read(path) != null
                ? throw new TransferException($"{path} exists", TransferErrorKind.AlreadyExists, side: TransferSide.Target)
                : Task.CompletedTask,
            OnComplete = _ =>
            {
                lock (_lock)
                {
                    _files.RemoveAll(file => string.Equals(file.Path, path, Comparison));
                    _files.Add((path, $"id{++_ids}", target.Session.Data));
                }
                return Task.CompletedTask;
            }
        };
        return target;
    }

    public Task DeleteAsync(StorageItem item, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _files.RemoveAll(file => item.IsFolder ? file.Path.StartsWith(item.Path + "/", Comparison) : file.Id == item.Id);
        }
        return Task.CompletedTask;
    }

    public Task<StorageItem> MoveAsync(StorageItem item, string path, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = _files.FindIndex(file => file.Id == item.Id);
            _files[index] = _files[index] with { Path = path };
            return Task.FromResult(Describe(_files[index]));
        }
    }

    public Task<StorageItem?> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var file = _files.FirstOrDefault(file => file.Id == id);
            return Task.FromResult(file.Path == null ? null : Describe(file));
        }
    }

    private static StorageItem Describe((string Path, string Id, byte[] Content) file) =>
        new(file.Path, file.Path[(file.Path.LastIndexOf('/') + 1)..], IsFolder: false) { Id = file.Id, Size = file.Content.Length, Version = "v1" };

    private static StorageItem Folder(string path) => new(path, path[(path.LastIndexOf('/') + 1)..], IsFolder: true);
}
