# Naqel

Naqel (ناقل, "carrier") is becoming a download and upload manager with two implementations that share all their logic. The desktop app is complete on its own, and can also connect to a Naqel Server and act as its UI. The server is the same app as a self-hosted service, with a REST API and a web UI. Both are for one user with one setup, and build on reusable .NET libraries that anyone can use on their own. The libraries exist; the apps don't yet.

## Layout

| Path | What |
|---|---|
| `src/Naqel.Core` | Package `Naqel.Core`, namespace `Naqel`: the transfer engine, contracts, resolvers, storages (`Naqel.Storage`), `Naqel.Files`, `Naqel.Streams` |
| `src/Naqel.Http` | Package `Naqel.Http`: HTTP sources and targets, `BearerTokenHandler`, `ChunkedUploadTarget`, HTTP resolvers |
| `src/Compat` | Polyfills compiled into each library on netstandard |
| `src/Naqel.Plugins` | The plugin contracts: the engine contract and the hooks (net10.0, API list kept, not packed yet) |
| `src/Naqel.Manager` | The job manager, storage locations and Naqel's own engine (net10.0, not a package) |
| `src/Naqel.Data` | Planned: the job store with EF Core |
| `src/Naqel.Contracts`, `Naqel.Application`, `Naqel.Client` | Planned: the operations and events the UI uses, implemented in-process and over the server's API |
| `src/Naqel.Presentation`, `Naqel.Desktop` | Planned: the desktop UI's view models, and the desktop app with an Avalonia UI |
| `src/Naqel.Server`, `Naqel.Web` | Planned: the server (REST API, SignalR), and its Vue.js web UI |
| `samples/Naqel.Samples` | Console sample against real public servers |
| `samples/Naqel.ConsoleHost` | Drives the job manager from the console with real links |
| `tools/Sync-PublicApi.ps1` | Updates the `PublicAPI.Unshipped.txt` lists from the build |
| `tests/Naqel.Core.Tests` | Tests for both libraries: net10.0 (net8.0 build) and net48 (netstandard2.0 build) |
| `tests/Naqel.Manager.Tests` | The manager with fake engines, and Naqel's engine with real files |
| `tests/Naqel.Soak` | Long runs against real servers, net10.0 and net48 |
| `docs/architecture.md` | The plan for the app: plugin kinds, contracts, engines, phases |
| `docs/design.md`, `docs/releasing.md` | How the libraries work and why; how to release |

## Commands

```bash
dotnet build Naqel.slnx
dotnet test --solution Naqel.slnx
dotnet test --project tests/Naqel.Core.Tests -f net10.0 -p:NaqelTarget=netstandard2.1
dotnet pack Naqel.slnx -c Release -o artifacts
dotnet run --project samples/Naqel.Samples -- all
dotnet run --project samples/Naqel.ConsoleHost -- <links> --to ./downloads
pwsh tools/Sync-PublicApi.ps1
dotnet run --project tests/Naqel.Soak -f net10.0 -- --minutes 2
```

Test output on this machine is in German ("gesamt", "fehlgeschlagen").

## Conventions

- **Commits:** the repository is public. Use the repo-local identity (`61435433+Proless@users.noreply.github.com`) and no AI attribution or co-author lines. One commit per topic. Don't push, tag or publish unless asked.
- **Line endings:** files are CRLF. The Write tool and `sed -i` produce LF, so normalize new or edited files: `perl -pi -e 's/(?<!\r)\n/\r\n/' <files>`.
- **Public API:** every public member is listed in each package's `PublicAPI.Unshipped.txt`. A change fails the build with RS0016 (missing) or RS0017 (removed) until the list matches; `pwsh tools/Sync-PublicApi.ps1` updates the lists (run it until it reports no changes), then review the diff.
- **Build rules:** warnings are errors, and the libraries use `AnalysisMode=Recommended`. Every public member needs XML docs.
- **Targets:** the libraries build for netstandard2.0, netstandard2.1 and net8.0, with the same API. Consumers need C# 9 or later.
- **Packages:** local I/O with no dependencies stays in `Naqel.Core`. Every remote protocol or service gets its own package built only on the public contracts. Resolvers are core abstractions; there is no registry.
- **Tests:** prefer deterministic fakes (in-memory sources and targets, fake `HttpMessageHandler`s). Prove a new test can fail by planting the bug it guards against, then restore the code. Also run the samples or soak test against real servers when HTTP behavior changes.
- **Docs:** update `docs/design.md`, `src/README.md` (the packages' readme), `README.md` (about Naqel as a whole) and `CHANGELOG.md` with every user-visible change.
- **Working style:** one phase at a time, with tests. Explain the change and wait for review before moving on.

## TODO

### Before the first release (0.1.0-preview.1)

- [ ] Check that the NuGet IDs `Naqel.Core` and `Naqel.Http` are free.
- [ ] Look into the name clash with the courier company Naqel Express before investing in branding.
- [ ] Add the `NUGET_API_KEY` secret to the repository's `nuget` environment.
- [ ] Release as described in `docs/releasing.md`: soak test on both runtimes, release date in the changelog, move `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`, tag `v0.1.0-preview.1`.

### The manager app

The plan is [docs/architecture.md](docs/architecture.md); its phases, in order:

- [x] 1. Manager foundations: `Naqel.Plugins` with the engine contract and hooks, `Naqel.Manager` with jobs, queue, routing, job events, completion steps and job data, the Naqel engine adapter, and a console host.
- [x] 2. Storage: `IStorage` and generic storage resolvers in `Naqel.Core`, tested against a fake storage service; storage locations in the manager.
- [ ] 3. Plugin host: the catalog with NuGet sources, manifests, a load context per plugin, ordering, plugin settings, accounts and asking the user.
- [ ] 4. Contracts, application and data: `Naqel.Contracts` with every operation and event, `Naqel.Application` implementing it, `Naqel.Data` with EF Core and SQLite.
- [ ] 5. Desktop: `Naqel.Presentation` and `Naqel.Desktop`, standalone, with an Avalonia UI on Windows, macOS and Linux, tray, notifications, link handling and file pickers.
- [ ] 6. Server: `Naqel.Server` with the REST API, OpenAPI, SignalR, an optional password and API keys; `Naqel.Client`; a Docker image.
- [ ] 7. Web UI: `Naqel.Web` with Vue.js and TypeScript, on a client generated from the OpenAPI description.
- [ ] 8. Desktop connected to a server, through `Naqel.Client`, with version checks.
- [ ] 9. BitTorrent with MonoTorrent.
- [ ] 10. Content kinds and choices, `Naqel.Hls`, and a first site plugin, such as a podcast feed.
- [ ] 11. More protocols and services, and the browser extension.

Decisions, also in section 9 of the plan:

- The desktop app uses Avalonia, for Windows, macOS and Linux. The server's web UI is Vue.js, not wrapped in a desktop shell.
- The desktop app shows one connection at a time (this computer or one server), with no combined view.
- One user and one setup, on the desktop and the server; a password on the server is optional.
- MonoTorrent is the first engine besides Naqel's own.
- Plugins come from a catalog of sources such as NuGet feeds, each in its own `AssemblyLoadContext`.
- No scheduling.
- Features planned for later versions build on the hooks (job events, completion steps, job data, operations for plugins). Keep the core abstractions open for them, without designing those features into the core.

### Libraries

- [ ] Update the design doc's roadmap (section 9): drop the URL registry, which resolvers replaced. Answer open question 2 (batches): that's the manager's job.
- [ ] Optional: add a `WithAdaptiveSplitting` builder method.
- [ ] Optional: smarter split sizes. Today a split always halves what's left of the slowest part.
- [ ] Protocol packages from the roadmap:
  - [ ] `Naqel.Tus`
  - [ ] `Naqel.Sftp`
  - [ ] `Naqel.Ftp`
  - [ ] `Naqel.S3`
  - [ ] `Naqel.AzureBlob`
- [ ] `Naqel.Extensions` for dependency injection and `Microsoft.Extensions.Logging`.
