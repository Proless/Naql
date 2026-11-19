# Naql

[![CI](https://github.com/Proless/Naql/actions/workflows/ci.yml/badge.svg)](https://github.com/Proless/Naql/actions/workflows/ci.yml)

Naql (نقل, "transfer") is a .NET library that moves data from a **source** to a **target**: a download, an upload, or a copy between two servers is the same operation. The engine does what every transfer needs, for every protocol:

- parallel parts over several connections, when the source supports ranges
- retries with backoff, continuing a part where it stopped when both sides allow it
- response and idle timeouts, even for streams that ignore cancellation
- a speed limit for one transfer or shared by many
- progress reports, and errors that say what went wrong, on which side, and whether retrying can help
- traces and metrics through .NET's `ActivitySource` and `Meter` (named `"Naql"`), ready for OpenTelemetry or `dotnet-counters`
- checkpoints that continue an unfinished transfer later, even after a restart
- checksums (SHA-2, SHA-1, MD5, or the server's `Repr-Digest`) verified before the result becomes visible
- noticing when the content changes during a transfer, and starting over

| Package | Contents |
|---|---|
| `Naql` | The engine, file sources and targets (`Naql.Files`), and stream sources and targets (`Naql.Streams`) |
| `Naql.Http` | HTTP and HTTPS downloads (with ranges and `If-Range`) and uploads (PUT, POST, multipart forms) |

The packages run on .NET 8 and later, .NET 5 to 7, .NET Core 3.x, Mono, Unity and .NET Framework 4.6.2 and later. The .NET 8 build has no dependencies; the netstandard builds bring a few Microsoft packages such as `System.Diagnostics.DiagnosticSource`. The API uses records and `init` setters, so it needs C# 9 or later: in a .NET Framework project, set `<LangVersion>9.0</LangVersion>` or later.

The first release is a preview, so install it with `--prerelease`:

```bash
dotnet add package Naql.Http --prerelease
```

`Naql.Http` brings in `Naql`; install only `Naql` for file and stream transfers.

## Usage

Download a file, naming it from the server's `Content-Disposition` or the URL:

```csharp
using Naql;
using Naql.Files;
using Naql.Http;

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
// Saves the checkpoint to file.zip.naql-checkpoint while running and when stopped, and deletes it once complete.
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
    // ex.Kind: NotFound, AccessDenied, Network, Timeout, SourceChanged, AlreadyExists, ...
    // ex.Side: Source or Target; ex.IsTransient: whether a later attempt could succeed
    // ex.Error: the failure itself, such as an HttpTransferException with its StatusCode
    // ex.PartFailures: every part that failed, with its range and number of attempts
}
```

Settings live in an immutable `TransferOptions` record: `MaxConnections`, `MaxBytesPerSecond`, `Retry` (a `RetryPolicy`), `ResponseTimeout`, `IdleTimeout` and more. Pass them with `WithOptions`, or give a `TransferClient` defaults and a `BandwidthLimiter` its transfers share. For HTTP authentication and headers, create an `HttpTransport` with your `HttpClient` and a `ConfigureRequest` callback such as `HttpTransport.BasicAuthentication(user, password)`.

A new protocol implements `ITransferSource` (describe the content, open a range of it) or `ITransferTarget` (write parts, then complete), and gets everything above without more code. [The design](https://github.com/Proless/Naql/blob/main/docs/design.md) explains the contracts and the engine.

## Build and test

Requires the .NET 10 SDK or later.

```bash
dotnet build
dotnet test
```

Tests run on Microsoft.Testing.Platform, on .NET 10 against the net8.0 build and, on Windows, on .NET Framework 4.8 against the netstandard2.0 build. Besides examples, they include property-based tests (with CsCheck) that generate transfers with random faults and cancellations; a failure prints the seed to replay it. To test the netstandard2.1 build, which no current runtime picks by itself:

```bash
dotnet test --project tests/Naql.Tests -f net10.0 -p:NaqlTarget=netstandard2.1
```

To collect code coverage:

```bash
dotnet test --coverage --coverage-output-format cobertura
```

`tests/Naql.Soak` runs transfers against real servers for a while (parallel, throttled, canceled and resumed, checksum-verified, uploaded) and checks every result; it builds for .NET 10 and .NET Framework 4.8, in C# 9:

```bash
dotnet run --project tests/Naql.Soak -c Release -f net10.0 -- --minutes 10
```

## Samples

`samples/Naql.Samples` is a console app that shows how to use the library and runs it against real servers.

Download your own files, optionally in parallel parts and with a speed limit. If a download stops, even because the process was killed, running the same command again continues it:

```bash
dotnet run --project samples/Naql.Samples -- get https://example.com/file.zip --parallel 4 --limit 1024 --out ./downloads
```

Upload a file with PUT, POST or a multipart form; without a URL it goes to httpbin.org, which echoes what it received:

```bash
dotnet run --project samples/Naql.Samples -- upload ./photo.jpg --method form
```

Run the demo scenarios against public test servers (ping.online.net, httpbin.org, speed.cloudflare.com):

```bash
dotnet run --project samples/Naql.Samples -- all
```

The default test file is served over plain HTTP. To use HTTPS, or if that server is unreachable, pass another file that supports byte ranges with `--url`, for example `--url https://fra-de-ping.vultr.com/vultr.com.100MB.bin` (100 MB).

The scenarios are `simple`, `parallel`, `resume`, `throttle`, `auth`, `upload` and `errors`; `help` lists every command and option. Ctrl+C cancels the running transfer.
