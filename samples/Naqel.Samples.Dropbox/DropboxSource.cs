using System.Diagnostics.CodeAnalysis;
using Naqel.Http;

namespace Naqel.Samples.Dropbox;

/// <summary>
/// A Dropbox file to download. It asks for a temporary link only when the transfer starts, not when the file is
/// resolved, so resolving a large folder costs one listing instead of a call per file, and links don't run out while
/// transfers wait in a queue. The download itself is an <see cref="HttpSource"/>, with all it does: ranges, parallel
/// parts, resuming. When a link runs out mid-transfer, it gets a new one.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "The lock never creates a wait handle, so it holds nothing to release")]
public sealed class DropboxSource : ITransferSource
{
    private readonly DropboxClient _client;
    private readonly SemaphoreSlim _linking = new(1, 1);
    private HttpSource? _download;

    /// <summary>
    /// A source for <paramref name="entry"/>, a file.
    /// </summary>
    public DropboxSource(DropboxClient client, DropboxEntry entry)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        if (!entry.IsFile)
        {
            throw new ArgumentException($"'{entry.Path}' is a folder", nameof(entry));
        }
    }

    /// <summary>
    /// The file.
    /// </summary>
    public DropboxEntry Entry { get; }

    /// <inheritdoc />
    public async Task<SourceInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        var download = await DownloadAsync(cancellationToken).ConfigureAwait(false);
        return await download.GetInfoAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SourceRead> OpenReadAsync(ReadRequest request, CancellationToken cancellationToken)
    {
        var download = await DownloadAsync(cancellationToken).ConfigureAwait(false);
        return await download.OpenReadAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // The first call gets the link; parts that start together wait for it instead of asking for links of their own
    private async Task<HttpSource> DownloadAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _download) is { } ready)
        {
            return ready;
        }

        await _linking.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_download == null)
            {
                // By id, so a link still comes when the file was moved or renamed since it was resolved
                var file = Entry.Id ?? Entry.Path;
                var link = await _client.GetTemporaryLinkAsync(file, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _download, _client.Links.Get(link, new HttpSourceOptions
                {
                    Identity = Entry.Identity,
                    Name = Entry.Name,
                    Refresh = token => _client.GetTemporaryLinkAsync(file, token)
                }));
            }
            return _download!;
        }
        finally
        {
            _linking.Release();
        }
    }
}
