using System.Runtime.CompilerServices;

namespace Naqel.Samples.Dropbox;

/// <summary>
/// Resolves <c>dropbox:</c> references into downloads: a file into one source, a folder into a source for each of its
/// files, with its path inside the folder, so a whole folder copies through <see cref="Resolve"/> to any target.
/// </summary>
public sealed class DropboxSourceResolver : ISourceResolver
{
    /// <summary>
    /// A resolver for the account <paramref name="client"/> signs in to, including the files of subfolders unless
    /// <paramref name="recursive"/> is <c>false</c>.
    /// </summary>
    public DropboxSourceResolver(DropboxClient client, bool recursive = true)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Recursive = recursive;
    }

    /// <summary>
    /// The account.
    /// </summary>
    public DropboxClient Client { get; }

    /// <summary>
    /// Whether a folder includes the files of its subfolders.
    /// </summary>
    public bool Recursive { get; }

    /// <inheritdoc />
    public bool CanResolve(Uri reference) => DropboxPath.IsDropbox(reference);

    /// <inheritdoc />
    public async IAsyncEnumerable<Resolved> ResolveAsync(Uri reference, ResolveContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var path = DropboxPath.FromReference(reference);
        var entry = await Client.GetMetadataAsync(path, cancellationToken).ConfigureAwait(false);
        if (entry.IsFile)
        {
            yield return new ResolvedSource(new DropboxSource(Client, entry), "", entry.ToSourceInfo());
            yield break;
        }

        // Dropbox matches paths without regard to case, and lists them with the case they were created with
        var folder = entry.Path;
        await foreach (var file in Client.ListFilesAsync(folder, Recursive, cancellationToken).ConfigureAwait(false))
        {
            var relative = file.Path[folder.Length..].TrimStart('/');
            yield return new ResolvedSource(new DropboxSource(Client, file), relative, file.ToSourceInfo());
        }
    }
}
