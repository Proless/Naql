using System;
using System.Threading;
using System.Threading.Tasks;

namespace Naqel.Storage;

/// <summary>
/// A target that writes a file in a storage, and says which storage and path, so what it wrote can be found again,
/// such as to delete it.
/// </summary>
public sealed class StorageTarget : ITransferTarget
{
    /// <summary>
    /// The target <paramref name="target"/>, which writes <paramref name="path"/> in <paramref name="storage"/> with
    /// <paramref name="mode"/>.
    /// </summary>
    public StorageTarget(IStorage storage, string path, WriteMode mode, ITransferTarget target)
    {
        Storage = storage ?? throw new ArgumentNullException(nameof(storage));
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Mode = mode;
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    /// <summary>
    /// The storage the file goes to.
    /// </summary>
    public IStorage Storage { get; }

    /// <summary>
    /// The file's path in the storage, before a <see cref="WriteMode.KeepBoth"/> write gives it another name.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// What the write does when a file is there already. With <see cref="WriteMode.KeepBoth"/>, the file written may
    /// have another name than <see cref="Path"/>, and the one at <see cref="Path"/> may be another file.
    /// </summary>
    public WriteMode Mode { get; }

    /// <summary>
    /// The storage's own target for the file.
    /// </summary>
    public ITransferTarget Target { get; }

    /// <inheritdoc />
    public TargetCapabilities Capabilities => Target.Capabilities;

    /// <inheritdoc />
    public Task<ITargetSession> OpenAsync(TargetOpenRequest request, CancellationToken cancellationToken) => Target.OpenAsync(request, cancellationToken);
}
