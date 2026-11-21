# Naqel

[![CI](https://github.com/Proless/Naqel/actions/workflows/ci.yml/badge.svg)](https://github.com/Proless/Naqel/actions/workflows/ci.yml)

Naqel (ناقل, "carrier") is a download and upload manager. It comes as a desktop app for Windows, macOS and Linux, and as the same app run as a self-hosted server, with a web UI and a REST API, for a home server, a NAS or Docker. The desktop app works on its own, and can also connect to a Naqel Server and manage it. Plugins add protocols, sites and services, and engines for protocols such as BitTorrent.

> **Status:** in development. The .NET libraries Naqel is built on, `Naqel.Core` and `Naqel.Http`, work and are tested, and anyone can use them on their own. The desktop app and the server are planned; [the architecture plan](docs/architecture.md) describes them and the order they'll be built in.

## What Naqel will do

- **Manage downloads and uploads in one place:** one queue, one place to set speed limits and connections, and the same progress and errors for every kind of job.
- **Many protocols:** HTTP first, then FTP, SFTP, WebDAV, SMB, S3 and tus, each a plugin that follows its standard.
- **Many sites and services:** plugins for cloud drives, file hosters, and video, audio and podcast sites, which know each site's links, sign-in and content, such as the qualities a video comes in.
- **Other engines:** BitTorrent through MonoTorrent, managed like any other job, and room for more engines behind the same contract.
- **Storage locations:** save to local folders, network shares, FTP, SFTP, WebDAV or cloud drives, set up once.
- **Desktop and server alike:** both are for one user with one setup and share all their logic. The desktop app has a native UI built with Avalonia; the server has a Vue.js web UI, and a REST API for other apps and automation.

```
 Naqel Desktop (Avalonia)                 Naqel Server (REST API, Vue.js web UI)
             └──────────── the same core ────────────┘
          jobs, queue, plugins, engines, storage locations
                               │
        Naqel.Core and protocol libraries such as Naqel.Http
```

## The libraries

The libraries move data from a **source** to a **target**: a download, an upload, or a copy between two servers is the same operation, and one engine does what every transfer needs. That includes parallel parts, retries that continue where they stopped, timeouts, speed limits, progress, checkpoints that survive a restart, and checksums.

| Package | Contents |
|---|---|
| `Naqel.Core` | The engine, file sources and targets (`Naqel.Files`), and stream sources and targets (`Naqel.Streams`) |
| `Naqel.Http` | HTTP and HTTPS downloads (with ranges and `If-Range`) and uploads (PUT, POST, multipart forms) |

```csharp
using Naqel;
using Naqel.Files;
using Naqel.Http;

var result = await Transfer
    .From(HttpSource.Get("https://example.com/file.zip"))
    .To(FileTarget.InDirectory("downloads"))
    .WithMaxConnections(4)
    .RunAsync();
```

They run on .NET 8 and later, .NET Framework 4.6.2 and later, and everything netstandard 2.0 and 2.1 cover. [The libraries' readme](src/README.md) shows how to use them, and [the design](docs/design.md) explains how they work. They aren't published to NuGet yet.

## Repository

| Path | What |
|---|---|
| `src/Naqel.Core`, `src/Naqel.Http` | The libraries |
| `src/Naqel.Plugins`, `src/Naqel.Manager` | The app's plugin contracts and its job manager, with Naqel's own engine |
| `samples/Naqel.Samples` | A console app that uses the libraries against real servers |
| `samples/Naqel.ConsoleHost` | A console app that runs the job manager with real links |
| `tests` | Unit and property-based tests of the libraries and the manager, and a soak test against real servers |
| `docs/architecture.md` | The plan for the desktop app, the server and plugins |
| `docs/design.md` | How the libraries work and why |

## Build and test

Requires the .NET 10 SDK or later.

```bash
dotnet build
dotnet test
```

Tests run on Microsoft.Testing.Platform, on .NET 10 against the net8.0 build and, on Windows, on .NET Framework 4.8 against the netstandard2.0 build. Besides examples, they include property-based tests (with CsCheck) that generate transfers with random faults and cancellations; a failure prints the seed to replay it. To test the netstandard2.1 build, which no current runtime picks by itself:

```bash
dotnet test --project tests/Naqel.Core.Tests -f net10.0 -p:NaqelTarget=netstandard2.1
```

To collect code coverage:

```bash
dotnet test --coverage --coverage-output-format cobertura
```

`tests/Naqel.Soak` runs transfers against real servers for a while (parallel, throttled, canceled and resumed, checksum-verified, uploaded, and with limits changed while running) and checks every result; it builds for .NET 10 and .NET Framework 4.8, in C# 9:

```bash
dotnet run --project tests/Naqel.Soak -c Release -f net10.0 -- --minutes 10
```

## Samples

`samples/Naqel.Samples` is a console app that shows how to use the libraries and runs them against real servers.

Download your own files, optionally in parallel parts and with a speed limit. If a download stops, even because the process was killed, running the same command again continues it:

```bash
dotnet run --project samples/Naqel.Samples -- get https://example.com/file.zip --parallel 4 --limit 1024 --out ./downloads
```

Upload a file with PUT, POST or a multipart form; without a URL it goes to httpbin.org, which echoes what it received:

```bash
dotnet run --project samples/Naqel.Samples -- upload ./photo.jpg --method form
```

Run the demo scenarios against public test servers (ping.online.net, httpbin.org, speed.cloudflare.com):

```bash
dotnet run --project samples/Naqel.Samples -- all
```

The default test file is served over plain HTTP. To use HTTPS, or if that server is unreachable, pass another file that supports byte ranges with `--url`, for example `--url https://fra-de-ping.vultr.com/vultr.com.100MB.bin` (100 MB).

The scenarios are `simple`, `parallel`, `resume`, `throttle`, `auth`, `upload` and `errors`; `help` lists every command and option. Ctrl+C cancels the running transfer.

## License

[MIT](LICENSE.md)
