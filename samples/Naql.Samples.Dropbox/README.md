# Naql.Samples.Dropbox

A sample package for one service, Dropbox, built only on Naql's public API. It isn't published. It shows how a package for a site or service builds on `Naql.Http`, and proves that it can be done without Naql's internals.

```csharp
using Naql;
using Naql.Files;
using Naql.Samples.Dropbox;

// Tokens come from the app's sign-in; the client adds them and asks for a fresh one when Dropbox rejects one
var dropbox = DropboxClient.Create(account.GetTokenAsync);
ISourceResolver[] sources = [new DropboxSourceResolver(dropbox), new FileSourceResolver()];
ITargetResolver[] targets = [new DropboxTargetResolver(dropbox), new FileTargetResolver()];

// Copies a whole folder, in either direction: dropbox: to file:, file: to dropbox:, or dropbox: to dropbox:
await foreach (var item in Resolve.SourcesAsync(new Uri("dropbox:/Photos"), sources))
{
    var target = await Resolve.TargetAsync(new Uri("file:///C:/Backup/Photos/"), item.Path, item.Info, targets);
    await Transfer.From(item.Source).To(target).WithMaxConnections(4).RunAsync();
}
```

## What each piece uses

| Piece | Built on | What it does |
|---|---|---|
| `DropboxClient` | `HttpTransport.SendAsync`, `HttpTransferException.FromResponse`, `BearerTokenHandler` | Calls the API, and turns Dropbox's error summaries into error kinds: `path/not_found` is `NotFound`, `insufficient_space` is `InsufficientStorage`, `path/conflict` is `AlreadyExists`, `too_many_write_operations` is a transient `RateLimited`. Temporary links are downloaded without the token, so it never reaches the download host. |
| `DropboxSourceResolver` | `ISourceResolver` | Turns `dropbox:/file` into one source and `dropbox:/folder` into one per file, page by page, each with its path inside the folder. |
| `DropboxSource` | `HttpSource` with `HttpSourceOptions` | Asks for a temporary link only when its transfer starts, so resolving a large folder costs one listing. The download's `Identity` is the file's id, so a checkpoint still fits a new link in a later run, and `Refresh` gets a new link when one runs out mid-transfer. |
| `DropboxTarget` | `ChunkedUploadTarget` | Starts an upload session, appends chunks and finishes it into the file. Dropbox can't be asked where a session stands, but it names the right offset when a chunk arrives at the wrong one, which answers both the query of a later run and a chunk whose answer was lost. |
| `DropboxTargetResolver` | `ITargetResolver`, `RelativePath` | Puts each relative path inside the destination folder, and refuses paths that would leave it. |

Everything else comes from the engine: parallel parts for downloads, retries, timeouts, the speed limit, progress, checkpoints, and continuing an upload session after a failure or in a later run.

## Gaps it found

Writing the sample found two gaps in `Naql.Http`. Both are closed:

- **`BearerTokenHandler` never resent a request with a body**, so an API call such as listing a folder failed when the token expired outside a transfer, where no retry covers it. Now it resends bodies held in memory (`StringContent` and other `ByteArrayContent`) with the fresh token at once; only streamed bodies are left to the transfer's retry.
- **The finish step didn't know how many bytes the service had**, which Dropbox's `finish` needs, and the source's length isn't always known. `ChunkedUpload.Acknowledged` now says it.

## What a real package would add

- Signing in: OAuth with PKCE, and storing and refreshing the tokens.
- Shared links (`https://www.dropbox.com/s/...`) resolved into their files.
- Checking Dropbox's `content_hash`, which hashes 4 MiB blocks and isn't one of `Checksum`'s algorithms.
- Concurrent upload sessions, which take chunks in parallel in multiples of 4 MiB.

The tests in `tests/Naql.Samples.Dropbox.Tests` run the sample against a fake Dropbox API, so they need no account.
