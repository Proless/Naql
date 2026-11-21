using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Naqel.Storage;

namespace Naqel.Files;

/// <summary>
/// A local folder as a storage, such as a downloads folder or a network share the OS has mounted. Paths stay inside
/// the folder.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Paths ignore case on Windows and macOS, as their file systems usually do, and keep it on Linux.</item>
/// <item>Files a transfer is still writing (<see cref="FileTarget.PartialExtension"/>) and checkpoints
/// (<see cref="FileCheckpointStore.Extension"/>) aren't listed, since they aren't content.</item>
/// <item>Written names are made safe for every OS, as <see cref="FileTarget.InDirectory"/> does.</item>
/// </list>
/// </remarks>
public sealed class FileStorage : IStorage, IStorageDelete, IStorageMove, IStorageCopy
{
    private static readonly bool IgnoresCase = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>
    /// The folder <paramref name="root"/> as a storage. It needn't exist yet; writing creates it.
    /// </summary>
    public FileStorage(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Length == 0)
        {
            throw new ArgumentException("A storage needs a folder", nameof(root));
        }
        Root = Path.GetFullPath(root);
    }

    /// <summary>
    /// The folder, as a full path.
    /// </summary>
    public string Root { get; }

    /// <inheritdoc />
    public StorageCapabilities Capabilities { get; } = new() { IgnoresCase = IgnoresCase };

    /// <inheritdoc />
    public Task<StorageItem?> GetAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(Find(RelativePath.Split(path)));
        }
        catch (Exception ex) when (FileErrors.Map(ex, TransferSide.Source) is { } error)
        {
            throw error;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StorageItem> ListAsync(string folder, bool recursive, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var found = await GetAsync(folder, cancellationToken).ConfigureAwait(false);
        if (found is not { IsFolder: true })
        {
            throw new TransferException($"There's no folder '{folder}' in {Root}", TransferErrorKind.NotFound, side: TransferSide.Source);
        }

        // Depth first, in name order, so a listing comes out the same each time: a folder comes right before what's in it
        var pending = new Stack<(FileSystemInfo Entry, string Path)>();
        PushChildren(pending, new DirectoryInfo(FullPath(found.Path)), found.Path);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (entry, path) = pending.Pop();
            if (entry is DirectoryInfo subfolder)
            {
                yield return Folder(subfolder, path);
                if (recursive)
                {
                    PushChildren(pending, subfolder, path);
                }
            }
            else if (entry is FileInfo file && !IsInProgress(file))
            {
                yield return Describe(file, path);
            }
        }
    }

    private static void PushChildren(Stack<(FileSystemInfo Entry, string Path)> pending, DirectoryInfo directory, string path)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. directory.EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (FileErrors.Map(ex, TransferSide.Source) is { } error)
        {
            throw error;
        }
        // Pushed last to first, so the first comes out first
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            pending.Push((entries[i], RelativePath.Combine(path, entries[i].Name)));
        }
    }

    /// <inheritdoc />
    public ITransferSource OpenRead(StorageItem file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return FileSource.At(FullPath(file.Path));
    }

    /// <inheritdoc />
    public ITransferTarget OpenWrite(string path, WriteMode mode, SourceInfo? info)
    {
        var conflict = mode switch
        {
            WriteMode.Fail => FileConflict.Fail,
            WriteMode.KeepBoth => FileConflict.KeepBoth,
            _ => FileConflict.Overwrite
        };
        return FileTarget.At(WritePath(path), conflict);
    }

    /// <inheritdoc />
    public Task DeleteAsync(StorageItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var full = FullPath(item.Path);
        if (full == Root)
        {
            throw new TransferException("The storage's own folder can't be deleted", TransferErrorKind.Unsupported, side: TransferSide.Target);
        }
        try
        {
            if (File.Exists(full))
            {
                File.Delete(full);
            }
            else if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
        }
        catch (Exception ex) when (FileErrors.Map(ex, TransferSide.Target) is { } error)
        {
            throw error;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<StorageItem> MoveAsync(StorageItem item, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var (from, to, moved) = Prepare(item, path);
        try
        {
            if (item.IsFolder)
            {
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to);
            }
        }
        catch (Exception ex) when (FileErrors.Map(ex, TransferSide.Target) is { } error)
        {
            throw error;
        }
        return Task.FromResult(Find(RelativePath.Split(moved))!);
    }

    /// <inheritdoc />
    public Task<StorageItem> CopyAsync(StorageItem file, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.IsFolder)
        {
            throw new TransferException("Only files can be copied within a folder storage", TransferErrorKind.Unsupported, side: TransferSide.Target);
        }
        var (from, to, copied) = Prepare(file, path);
        try
        {
            File.Copy(from, to, overwrite: false);
        }
        catch (Exception ex) when (FileErrors.Map(ex, TransferSide.Target) is { } error)
        {
            throw error;
        }
        return Task.FromResult(Find(RelativePath.Split(copied))!);
    }

    private static bool IsInProgress(FileInfo file) =>
        file.Name.EndsWith(FileTarget.PartialExtension, StringComparison.OrdinalIgnoreCase) ||
        file.Name.EndsWith(FileCheckpointStore.Extension, StringComparison.OrdinalIgnoreCase);

    // The item at the path, with the case it has on disk
    private StorageItem? Find(IReadOnlyList<string> segments)
    {
        if (!Directory.Exists(Root))
        {
            return null;
        }
        var directory = new DirectoryInfo(Root);
        var path = "";
        for (var i = 0; i < segments.Count; i++)
        {
            var entry = Entry(directory, segments[i]);
            if (entry == null)
            {
                return null;
            }
            path = RelativePath.Combine(path, entry.Name);
            if (entry is FileInfo file)
            {
                return i == segments.Count - 1 && !IsInProgress(file) ? Describe(file, path) : null;
            }
            directory = (DirectoryInfo)entry;
        }
        return Folder(directory, path);
    }

    private static FileSystemInfo? Entry(DirectoryInfo directory, string name)
    {
        if (!IgnoresCase)
        {
            var exact = Path.Combine(directory.FullName, name);
            return File.Exists(exact) ? new FileInfo(exact) : Directory.Exists(exact) ? new DirectoryInfo(exact) : null;
        }
        // The name as the file system keeps it, whatever case it was asked in
        return directory.EnumerateFileSystemInfos().FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private (string From, string To, string Path) Prepare(StorageItem item, string path)
    {
        var from = FullPath(item.Path);
        var to = WritePath(path);
        if (File.Exists(to) || Directory.Exists(to))
        {
            throw new TransferException($"Something is at '{path}' already", TransferErrorKind.AlreadyExists, side: TransferSide.Target);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        var relative = string.Join("/", to.Substring(Root.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(segment => segment.Length > 0));
        return (from, to, relative);
    }

    private string FullPath(string path) => RelativePath.Split(path).Aggregate(Root, Path.Combine);

    // Names written to disk are made safe on every OS; RelativePath already keeps them inside the folder
    private string WritePath(string path)
    {
        var segments = RelativePath.Split(path);
        if (segments.Count == 0)
        {
            throw new TransferException("The storage's own folder can't be written as a file", TransferErrorKind.Unsupported, side: TransferSide.Target);
        }
        return segments.Select(segment => FileNames.ToSafeName(segment) ?? "_").Aggregate(Root, Path.Combine);
    }

    private static StorageItem Describe(FileInfo file, string path) => new(path, file.Name, IsFolder: false)
    {
        Size = file.Length,
        Modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
        Version = FileSource.VersionOf(file)
    };

    private static StorageItem Folder(DirectoryInfo directory, string path) =>
        new(path, directory.Name, IsFolder: true)
        {
            Modified = new DateTimeOffset(directory.LastWriteTimeUtc, TimeSpan.Zero)
        };
}
