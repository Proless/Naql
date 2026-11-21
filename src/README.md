# Naqel libraries

The .NET libraries behind [Naqel](https://github.com/Proless/Naqel), a download and upload manager, which anyone can use on their own. They move data from a **source** to a **target**: a download, an upload, or a copy between two servers is the same operation. Their engine does what every transfer needs, for every protocol:

- parallel parts over several connections, when the source supports ranges, with connections that free up taking over the end of slower parts
- retries with backoff, continuing a part where it stopped when both sides allow it
- response and idle timeouts, even for streams that ignore cancellation
- a speed limit for one transfer or shared by many, and a speed limit and connection count that can change while a transfer runs
- progress reports, down to every part's range, bytes and state, and errors that say what went wrong, on which side, and whether retrying can help
- traces and metrics through .NET's `ActivitySource` and `Meter` (named `"Naqel"`), ready for OpenTelemetry or `dotnet-counters`
- checkpoints that continue an unfinished transfer later, even after a restart
- checksums (SHA-2, SHA-1, MD5, or the server's `Repr-Digest`) verified before the result becomes visible
- noticing when the content changes during a transfer, and starting over

| Package | Contents |
|---|---|
| `Naqel.Core` | The engine, file sources and targets (`Naqel.Files`), and stream sources and targets (`Naqel.Streams`) |
| `Naqel.Http` | HTTP and HTTPS downloads (with ranges and `If-Range`) and uploads (PUT, POST, multipart forms) |

The packages run on .NET 8 and later, .NET 5 to 7, .NET Core 3.x, Mono, Unity and .NET Framework 4.6.2 and later. The .NET 8 build has no dependencies; the netstandard builds bring a few Microsoft packages such as `System.Diagnostics.DiagnosticSource`. The API uses records and `init` setters, so it needs C# 9 or later: in a .NET Framework project, set `<LangVersion>9.0</LangVersion>` or later.

The first release is a preview, so install it with `--prerelease`:

```bash
dotnet add package Naqel.Http --prerelease
```

`Naqel.Http` brings in `Naqel.Core`; install only `Naqel.Core` for file and stream transfers. The packages' namespaces start with `Naqel`: `Naqel`, `Naqel.Files`, `Naqel.Streams` and `Naqel.Http`.

## Usage

Download a file, naming it from the server's `Content-Disposition` or the URL:

```csharp
using Naqel;
using Naqel.Files;
using Naqel.Http;

var result = await Transfer
    .From(HttpSource.Get("https://example.com/file.zip"))
    .To(FileTarget.InDirectory("downloads"))
    .RunAsync();

Console.WriteLine($"{result.Transferred} bytes in {result.Duration} to {result.Location}");
```

Download over four connections with a speed limit and progress:

```csharp
var progress = new Progress<TransferProgress>(p => Console.WriteLine($"{p.Fraction:P0} at {p.CurrentRate / 1024:0} KB/s"));

await Transfer
    .From(HttpSource.Get(uri))
    .To(FileTarget.At("file.zip"))
    .WithMaxConnections(4)
    .WithMaxBytesPerSecond(1024 * 1024)
    .RunAsync(progress, cancellationToken);
```

Upload a file:

```csharp
await Transfer.From(FileSource.At("backup.tar")).To(HttpTarget.Put(uploadUri)).RunAsync();
await Transfer.From(FileSource.At("photo.jpg")).To(HttpTarget.PostForm(formUri, "file")).RunAsync();
```

Continue an unfinished download by itself, even after a crash or a restart:

```csharp
// Saves the checkpoint to file.zip.naqel-checkpoint while running and when stopped, and deletes it once complete.
// Running the same code again transfers only the missing bytes, or everything if the file changed meanwhile.
await Transfer
    .From(HttpSource.Get(uri))
    .To(FileTarget.At("file.zip"))
    .WithCheckpointStore(FileCheckpointStore.For("file.zip"))
    .RunAsync(progress, cancellationToken);
```

To keep checkpoints somewhere else, implement `ICheckpointStore`, or handle them yourself with `operation.GetCheckpoint()`, `TransferCheckpoint.Serialize()` and `ResumeFrom(checkpoint)`.

Verify the content before it gets its real name:

```csharp
await Transfer.From(HttpSource.Get(uri)).To(FileTarget.At("file.zip"))
    .WithExpectedChecksum(Checksum.Sha256("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"))
    .RunAsync();
```

Handle failures:

```csharp
try
{
    await Transfer.From(source).To(target).RunAsync();
}
catch (TransferFailedException ex)
{
    // ex.Kind: NotFound, Unauthorized, AccessDenied, Network, Timeout, RateLimited, SourceChanged, AlreadyExists, ...
    // ex.Side: Source or Target; ex.IsTransient: whether a later attempt could succeed
    // ex.Error: the failure itself, such as an HttpTransferException with its StatusCode
    // ex.PartFailures: every part that failed, with its range and number of attempts
}
```

Settings live in an immutable `TransferOptions` record: `MaxConnections`, `MaxBytesPerSecond`, `Retry` (a `RetryPolicy`), `ResponseTimeout`, `IdleTimeout` and more. Pass them with `WithOptions`, or give a `TransferClient` defaults and a `BandwidthLimiter` its transfers share. `TransferOperation.SetMaxBytesPerSecond` and `SetMaxConnections` change a transfer's limits while it runs. For HTTP authentication and headers, create an `HttpTransport` with your `HttpClient` and a `ConfigureRequest` callback such as `HttpTransport.BasicAuthentication(user, password)`.

## Extending

A new protocol implements `ITransferSource` (describe the content, open a range of it) or `ITransferTarget` (write parts, then complete), and gets everything above without more code. A package for a service on HTTP, such as a cloud drive or a file host, builds on `Naqel.Http`, with its resolvers, `BearerTokenHandler` for tokens and `ChunkedUploadTarget` for resumable uploads. A place where files live, such as a cloud drive or a server, implements `IStorage` (list, describe, read, write) and gets its resolving from `StorageSourceResolver` and `StorageTargetResolver`; `FileStorage` is a local folder as one. [The design](https://github.com/Proless/Naqel/blob/main/docs/design.md) explains the contracts and the engine.
