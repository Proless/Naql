# Naqel

Naqel (ناقل, "carrier") is becoming a download and upload manager app, built on reusable .NET libraries that anyone can use on their own. The libraries exist; the app doesn't yet.

## Layout

| Path | What |
|---|---|
| `src/Naqel.Core` | Package `Naqel.Core`, namespace `Naqel`: the transfer engine, contracts, resolvers, `Naqel.Files`, `Naqel.Streams` |
| `src/Naqel.Http` | Package `Naqel.Http`: HTTP sources and targets, `BearerTokenHandler`, `ChunkedUploadTarget`, HTTP resolvers |
| `src/Compat` | Polyfills compiled into each library on netstandard |
| `src/Naqel` | Planned: the manager app, namespace `Naqel.App` |
| `samples/Naqel.Samples` | Console sample against real public servers |
| `samples/Naqel.Samples.Dropbox` | Sample service package built only on the public API (not packed) |
| `tests/Naqel.Core.Tests` | Tests for both libraries: net10.0 (net8.0 build) and net48 (netstandard2.0 build) |
| `tests/Naqel.Samples.Dropbox.Tests` | The Dropbox sample against a fake Dropbox API |
| `tests/Naqel.Soak` | Long runs against real servers, net10.0 and net48 |
| `docs/design.md`, `docs/releasing.md` | How the libraries work and why; how to release |

## Commands

```bash
dotnet build Naqel.slnx
dotnet test --solution Naqel.slnx
dotnet test --project tests/Naqel.Core.Tests -f net10.0 -p:NaqelTarget=netstandard2.1
dotnet pack Naqel.slnx -c Release -o artifacts
dotnet run --project samples/Naqel.Samples -- all
dotnet run --project tests/Naqel.Soak -f net10.0 -- --minutes 2
```

Test output on this machine is in German ("gesamt", "fehlgeschlagen").

## Conventions

- **Commits:** the repository is public. Use the repo-local identity (`61435433+Proless@users.noreply.github.com`) and no AI attribution or co-author lines. One commit per topic. Don't push, tag or publish unless asked.
- **Line endings:** files are CRLF. The Write tool and `sed -i` produce LF, so normalize new or edited files: `perl -pi -e 's/(?<!\r)\n/\r\n/' <files>`.
- **Public API:** every public member is listed in each library's `PublicAPI.Unshipped.txt`. A change fails the build with RS0016 (missing) or RS0017 (removed) until the list matches.
- **Build rules:** warnings are errors, and the libraries use `AnalysisMode=Recommended`. Every public member needs XML docs.
- **Targets:** the libraries build for netstandard2.0, netstandard2.1 and net8.0, with the same API. Consumers need C# 9 or later.
- **Packages:** local I/O with no dependencies stays in `Naqel.Core`. Every remote protocol or service gets its own package built only on the public contracts. Resolvers are core abstractions; there is no registry.
- **Tests:** prefer deterministic fakes (in-memory sources and targets, fake `HttpMessageHandler`s). Prove a new test can fail by planting the bug it guards against, then restore the code. Also run the samples or soak test against real servers when HTTP behavior changes.
- **Docs:** update `docs/design.md`, `README.md` and `CHANGELOG.md` with every user-visible change.
- **Working style:** one phase at a time, with tests. Explain the change and wait for review before moving on.

## TODO

### Before the first release (0.1.0-preview.1)

- [ ] Check that the NuGet IDs `Naqel.Core` and `Naqel.Http` are free.
- [ ] Look into the name clash with the courier company Naqel Express before investing in branding.
- [ ] Add the `NUGET_API_KEY` secret to the repository's `nuget` environment.
- [ ] Release as described in `docs/releasing.md`: soak test on both runtimes, release date in the changelog, move `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`, tag `v0.1.0-preview.1`.

### The manager app

- [ ] Choose the UI framework: WPF or WinUI for Windows only, or Avalonia for cross-platform.
- [ ] Create `src/Naqel`, with the namespace `Naqel.App`, and its test project.
- [ ] Decide whether queue, job store, scheduling and accounts go into a UI-free `Naqel.Manager` library, so they stay testable and reusable by a CLI or service.
- [ ] Manager features that were out of scope for the libraries:
  - [ ] a queue
  - [ ] a persistent job store, with checkpoints
  - [ ] accounts and interactive sign-in
  - [ ] asking the user, for example about conflicts or credentials
- [ ] Use the live controls in the UI: `SetMaxBytesPerSecond`, `SetMaxConnections`, and per-part progress (`TransferProgress.Parts`).

### Libraries

- [ ] Add the PublicAPI sync script to the repository, for example `tools/Sync-PublicApi.ps1`. Today it only lives in a session scratchpad. It adds RS0016 symbols to `PublicAPI.Unshipped.txt` and removes RS0017 ones.
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

### Dropbox sample

- [ ] Run it against a real Dropbox account; so far it has only run against the fake API.
- [ ] To make it a real package:
  - [ ] OAuth sign-in with PKCE
  - [ ] resolving shared links
  - [ ] checking Dropbox's `content_hash`
  - [ ] concurrent upload sessions
