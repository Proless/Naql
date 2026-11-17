using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Files;

/// <summary>
/// Keeps a transfer's checkpoint in a small text file.
/// </summary>
/// <remarks>
/// Saving writes a temporary file and renames it over the old one, so a crash while saving never leaves a damaged
/// checkpoint. A file that can't be read as a checkpoint counts as no checkpoint, so the transfer starts over.
/// </remarks>
public sealed class FileCheckpointStore : ICheckpointStore
{
    /// <summary>
    /// Added to a file's path by <see cref="For(string)"/>.
    /// </summary>
    public const string Extension = ".naql-checkpoint";

    /// <summary>
    /// Keeps the checkpoint in the file at <paramref name="path"/>.
    /// </summary>
    public FileCheckpointStore(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    /// <summary>
    /// The full path of the checkpoint file.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// A store next to the file a transfer writes: <c>file.zip</c> keeps its checkpoint in <c>file.zip.naql-checkpoint</c>.
    /// </summary>
    public static FileCheckpointStore For(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new FileCheckpointStore(path + Extension);
    }

    /// <inheritdoc />
    public async Task<TransferCheckpoint?> LoadAsync(CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            return TransferCheckpoint.Deserialize(text);
        }
        catch (FormatException)
        {
            // A damaged or foreign file: starting over is the only safe choice
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(TransferCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + ".tmp";
        await File.WriteAllTextAsync(temporary, checkpoint.Serialize(), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, Path, overwrite: true);
    }

    /// <inheritdoc />
    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        // File.Delete accepts a missing file, but not a missing directory
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
        return Task.CompletedTask;
    }
}
