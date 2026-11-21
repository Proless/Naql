namespace Naqel.Samples.Dropbox;

/// <summary>
/// Resolves <c>dropbox:</c> destinations into uploads: <c>dropbox:/Backups</c> and <c>photos/a.jpg</c> upload to
/// <c>/Backups/photos/a.jpg</c>, creating the folders on the way, as Dropbox does.
/// </summary>
public sealed class DropboxTargetResolver : ITargetResolver
{
    /// <summary>
    /// A resolver for the account <paramref name="client"/> signs in to.
    /// </summary>
    /// <param name="client">The account to upload to</param>
    /// <param name="overwrite">Whether uploads replace files already there</param>
    /// <param name="chunkSize">The bytes in every chunk of an upload but the last</param>
    public DropboxTargetResolver(DropboxClient client, bool overwrite = true, int chunkSize = DropboxTarget.DefaultChunkSize)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Overwrite = overwrite;
        ChunkSize = chunkSize;
    }

    /// <summary>
    /// The account.
    /// </summary>
    public DropboxClient Client { get; }

    /// <summary>
    /// Whether uploads replace files already there.
    /// </summary>
    public bool Overwrite { get; }

    /// <summary>
    /// The bytes in every chunk of an upload but the last.
    /// </summary>
    public int ChunkSize { get; }

    /// <inheritdoc />
    public bool CanResolve(Uri destination) => DropboxPath.IsDropbox(destination);

    /// <inheritdoc />
    public ValueTask<ITransferTarget> ResolveAsync(Uri destination, string relativePath, SourceInfo? info, ResolveContext context, CancellationToken cancellationToken)
    {
        // RelativePath rejects ".." and the like, so the upload stays inside the destination
        var path = DropboxPath.Combine(DropboxPath.FromReference(destination), relativePath);
        if (path.Length == 0)
        {
            throw new TransferException("The Dropbox root is a folder; name a file to upload to", TransferErrorKind.Unsupported, side: TransferSide.Target);
        }
        return new ValueTask<ITransferTarget>(new DropboxTarget(Client, path, Overwrite, ChunkSize));
    }
}
