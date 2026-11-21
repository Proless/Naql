# Naqel app architecture

This is the plan for Naqel, the download and upload manager, and for how plugins extend it. Naqel comes in two implementations of the same app, which share all their logic:

- **Naqel Desktop:** the complete app on one computer, working on its own. It can also connect to a Naqel Server and act as the UI for it.
- **Naqel Server:** the same app as a self-hosted service, for a home server, a NAS or Docker, with a REST API and a web UI in the browser.

Both are for one user with one setup: the server isn't a multi-user system, just the desktop app's counterpart for a machine without a screen.

The libraries both build on, `Naqel.Core` and `Naqel.Http`, are described in [design.md](design.md). They stay usable on their own, without either app.

Nothing in this document is built yet. The phases at the end say in which order it would be. Many requirements come from the earlier Downloads Hub project and its requirements specification, which planned a server only. Here the desktop app is complete on its own, and the server is a second way to run the same app.

## 1. Goals

- **Many protocols:** HTTP first; FTP, SFTP, WebDAV, SMB, S3 and tus later. Each is generic, following its standard specification, and knows nothing about any site.
- **Many sites and services:** cloud drives, file hosters, video, audio and podcast sites. Each is a plugin built on the protocols, and handles its own site: links, sign-in, API, and what content it offers.
- **Many file types:** a plugin describes what its site delivers, such as video in several qualities, audio, or documents. The app sorts files by type and offers choices where a site has them.
- **More engines than Naqel's own:** protocols that are hard to implement, such as BitTorrent, run in engines that Naqel manages through the same contract as its own engine.
- **Storage locations:** places to save to, set up once and used by every engine and plugin: local folders, network shares, FTP, SFTP, WebDAV and cloud drives.
- **Open for what comes later:** plugins can follow and take part in every job's life through hooks, so later features build on the core instead of changing it.
- **One app, two ways to run it:** the desktop app and the server share everything but their UIs and what's specific to how they run, and the desktop app can manage a server as well as itself.
- **An API for everyone:** the server's REST API offers the same operations as the UI, so other apps and automation can do what the user can.

Out of scope: breaking DRM, processing files after a download, such as unpacking or converting, and scheduling, such as time windows or limits by time of day.

## 2. Layers

```
 Naqel.Desktop ─────────── Avalonia UI, tray, OS integration          Naqel.Web ───── Vue.js web UI, in the browser
     │                                                                     │  REST API and live updates
 Naqel.Presentation ────── the desktop UI's view models               Naqel.Server ── REST API, SignalR, optional sign-in
     │                                                                     │
 Naqel.Contracts ───────── every operation and event ──────────────────────┘
     │          implemented by
     ├── Naqel.Application ── in-process: the desktop's own core, and the server's
     │       │
     │   Naqel.Manager ────── jobs, queue, routing, storage locations, hooks, accounts, the plugin host
     │       │   one engine contract
     │       ├── Naqel engine ─ adapter over Naqel.Core, with the site, format and protocol plugins
     │       └── Other engines ── BitTorrent and others, each behind an adapter
     │
     └── Naqel.Client ─────── over the REST API: a server's core, for the desktop app's server connections
```

Each layer only uses the ones below it. A site plugin never handles bytes itself: it turns its links into sources and targets of the protocol and format plugins:

```
 Site and service plugins ── Dropbox, Google Drive, a file host, a video site, a podcast feed
 Format plugins ──────────── HLS, DASH, M3U, RSS
 Protocol plugins ────────── HTTP, FTP, SFTP, WebDAV, SMB, S3, tus
 Naqel.Core ──────────────── the transfer engine; transfer, resolver and storage contracts
```

## 3. One app, two implementations

### 3.1 What's shared

Everything below the UIs is the same in the desktop app and the server: the operations, jobs, the queue, plugins, engines, storage locations, hooks, accounts and the job store. Only the UIs are written twice, each in the technology native to where it runs:

- **One contract for everything the user can do.** `Naqel.Contracts` defines every operation, such as adding a link, pausing a job or setting a limit, and the events that report changes, such as progress or a job that needs attention. It has two implementations:
  - `Naqel.Application`, which runs the app's core in-process: in the desktop app, and inside the server.
  - `Naqel.Client`, which runs the same operations over the server's REST API and live updates.

  The desktop UI uses only this contract, so it manages the desktop's own jobs or a server's without knowing which. The server's REST API is the same contract over HTTP, and the web UI uses only that API.
- **Two native UIs.** The desktop app has a native desktop UI, built with Avalonia, for Windows, macOS and Linux. The server's web UI, `Naqel.Web`, is a Vue.js app in the browser. Neither wraps the other: the desktop app isn't a web view, and the web UI isn't a desktop app.
- **Keeping the two UIs small.** All logic stays below them, so a feature's behavior is written once and each UI only presents it:
  - **Desktop:** the desktop UI's view models live in `Naqel.Presentation`, which knows no UI framework. They can be tested without a window.
  - **Web:** the web UI's TypeScript client is generated from the server's OpenAPI description, so a change to the API shows up as a type error in the web UI, not as a bug.
  - **Both:** plugin settings, choices and requests for input are described as data, and each UI renders them. A plugin brings no UI code, and works in both UIs without changes.
- **Host differences behind small interfaces**, which `Naqel.Application` asks its host for:
  - how to ask the user something
  - where credentials are kept
  - how notifications reach the user
  - which paths may be written

### 3.2 Naqel Desktop

- **Standalone:** the desktop app runs `Naqel.Application` and everything below it inside the app. There's no server to install and no sign-in. It's complete without a server.
- **A native UI:** built with Avalonia on `Naqel.Presentation`, with one codebase for Windows, macOS and Linux. What differs by platform, such as registering for `magnet:` links, starting with the system and secure credential storage, sits behind small platform interfaces.
- **Connecting to a server:** the desktop app can also connect to a Naqel Server through `Naqel.Client`, and then manages that server's jobs, storage locations, plugins and settings with the same desktop UI. It shows one connection at a time: "This computer" or one of the saved servers, picked with a connection switcher. When a server has a password, the desktop app signs in with it or an API key, and keeps that credential in the OS's secure storage.
- **Native integration:**
  - a tray icon
  - starting with the system
  - OS notifications, also for a connected server's jobs
  - opening `magnet:` links and `.torrent` files, added to the computer or to the connected server
  - native file and folder pickers for local paths
  - a browser extension that hands over links
- **Paths:** any local folder on this computer. On a connected server, that server's storage locations.
- **Asking the user** happens right away, in a dialog, also for a connected server's jobs that need attention.

### 3.3 Naqel Server

- **The same app as a service:** the server runs `Naqel.Application`, and makes it reachable over a REST API with OpenAPI documentation, with live updates over SignalR. `Naqel.Client` is the typed .NET client for that API: the desktop app uses it, and so can other apps and tests.
- **The web UI:** `Naqel.Web`, a Vue.js app with TypeScript, which the server serves to browsers, so it can be managed without the desktop app, such as from a phone. It talks to the same REST API and live updates as any other client, so the API always covers what the web UI can do.
- **One user, optional sign-in:** like the desktop app, the server has one user and one setup. A password is optional. With one, the web UI asks for it, and the desktop app and other apps use it or an API key. Without one, anyone who can reach the server can use it, so the web UI says so, for a server that's reachable beyond a trusted network.
- **Paths:** the server saves to the storage locations set up on it, which can be any of its folders.
- **Running it:** usually as a Docker image, on Linux or Windows. Data such as the database, plugins and settings lives in one data folder, a volume in Docker. HTTPS comes from a reverse proxy in front.
- **Asking the user** doesn't block: a job that needs input, such as a password or a format choice, waits as "needs attention" until the user answers, in the web UI, the desktop app or through the API.

### 3.4 Versions

The desktop app and a server it connects to may be different versions. The REST API is versioned, the server says which versions of `Naqel.Contracts` it supports, and the desktop app tells the user when it can't manage a server that's too old or too new, instead of failing on single operations.

### 3.5 Data

- **The job store** keeps jobs with their labels and plugin data, the queue, storage locations, accounts, installed plugins and settings in a SQL database through EF Core: SQLite by default in both, and PostgreSQL as an option for the server.
- **Credentials** are encrypted: with the OS's secure storage on the desktop, and with ASP.NET Data Protection keys on the server, never in plain text.
- **Engine state:** an engine with its own resume data keeps it; the job store keeps which engine runs a job and the engine's id for it. Naqel's own engine keeps its checkpoints in the job store through `ICheckpointStore`.

## 4. Projects

| Project | Holds | Used by |
|---|---|---|
| `Naqel.Core` | The engine and the transfer and resolver contracts (exist). New: the storage contract with generic storage resolvers, and content kinds and choices | everything; a reusable library |
| `Naqel.Http`, later `Naqel.Ftp`, `Naqel.Sftp`, `Naqel.WebDav`, `Naqel.Smb`, `Naqel.S3`, `Naqel.Tus` | Protocols | plugins, and other developers |
| `Naqel.Hls`, later `Naqel.Dash`, `Naqel.Feeds` | Formats | site plugins |
| `Naqel.Plugins` | The plugin contracts: registration and manifest, the engine contract, hooks, accounts, asking the user | plugins, and the manager |
| `Naqel.Manager` | Jobs, queue, routing, storage locations, hooks, the plugin host and catalog, the adapter for Naqel's own engine | `Naqel.Application` |
| `Naqel.Data` | The job store with EF Core, SQLite and PostgreSQL | `Naqel.Application` |
| `Naqel.Contracts` | Every operation and event, with the data they carry | the desktop app, both implementations, the server's API, plugins |
| `Naqel.Application` | The in-process implementation of `Naqel.Contracts`, and the interfaces each host implements | the desktop app, the server |
| `Naqel.Client` | The implementation of `Naqel.Contracts` over the REST API | the desktop app, other apps, tests |
| `Naqel.Presentation` | The desktop UI's view models, with no UI framework | the desktop app |
| `Naqel.Desktop` | The desktop app: Avalonia UI, tray, OS integration, server connections | — |
| `Naqel.Server` | ASP.NET Core: REST API, OpenAPI, SignalR, optional sign-in, serving the web UI | — |
| `Naqel.Web` | The web UI: Vue.js with TypeScript, with a client generated from the OpenAPI description; built with npm into the server's static files | the server |

This replaces the single `src/Naqel` app project planned before. The libraries stay free of a registry, as decided for the resolvers: the manager's plugin host is where plugins are found and ordered.

## 5. Plugin kinds

| Kind | Does | Contracts it implements |
|---|---|---|
| Protocol | Reads and writes by a standard protocol; resolves its URL schemes | `ITransferSource`, `ITransferTarget`, `ISourceResolver`, `ITargetResolver` |
| Format | Turns a standard format into one source, such as an HLS playlist into its segments joined | `ISourceResolver`, often with `ConcatenatedSource` |
| Storage service | A drive-like service: browse, list, describe, read, write, and optionally delete and move | `IStorage` (new); the generic storage resolvers do the resolving |
| Site | Turns a site's links into what it offers: files, references to resolve further, or content with choices | `ISourceResolver`, with content kinds and choices (new) |
| Engine | Runs jobs itself, for protocols such as BitTorrent | `ITransferEngine` (new) |
| Hook | Follows job events, or adds a step when a job completes | `IJobObserver`, `ICompletionStep` (new) |
| Account | Signs in to a service and keeps its credentials fresh | `IAccountProvider` (new) |

One plugin package can bring several, such as a Dropbox plugin with a storage service and an account provider. Plugins run where the core runs: in the desktop app for its own jobs, and on the server for the server's. A desktop app connected to a server shows the server's plugins, not its own.

## 6. Contracts

The sketches show the shape; names and details are settled in each phase.

### 6.1 Storage (in Naqel.Core)

For drive-like services, so the UI can browse remote folders, storage locations can use any of them, and every drive gets the same resolvers and folder copies:

```csharp
public interface IStorage                           // one account on one service
{
    StorageCapabilities Capabilities { get; }       // ids, folders, case-insensitive paths, delete, move, trash, server-side copy
    Task<StorageItem?> GetAsync(string path, CancellationToken cancellationToken);
    IAsyncEnumerable<StorageItem> ListAsync(string folder, bool recursive, CancellationToken cancellationToken);
    ITransferSource OpenRead(StorageItem file);
    ITransferTarget OpenWrite(string path, WriteMode mode, SourceInfo? info);   // overwrite, rename or fail
}

public sealed record StorageItem(string Path, string? Id, string Name, bool IsFolder, long? Size, DateTimeOffset? Modified,
    string? Version, IReadOnlyDictionary<string, string> Hashes);
```

- Optional operations, such as delete, move and server-side copy, are capabilities with their own small interfaces, so a service implements only what it can.
- Items have an id as well as a path, for services such as Google Drive, which identify files by id, allow two files with the same name in one folder, and let a file sit in several folders. The design is checked against Google Drive before it's settled.
- `StorageSourceResolver` and `StorageTargetResolver` resolve any storage's references, so a storage service plugin implements only listing, describing, reading and writing. Tests prove the contract against a fake storage service with ids, paths and case-insensitive names.
- **Storage locations** in the manager are named, configured storages, such as "Media share" on an SMB server or "Backups" in a Dropbox folder. A local folder is one too, including a network share the OS has mounted.

### 6.2 Content kinds and choices (in Naqel.Core)

A site plugin says what it delivers before anything is downloaded:

- **The kind of content:** video, audio, image, document, archive, software, or other. A generic protocol plugin, which knows no site, leaves it to be worked out from `SourceInfo.ContentType` and the name.
- **Choices:** a third kind of resolver output next to `ResolvedSource` and `ResolvedReference`. It lists the variants, such as formats with quality, container, size and language, and opens the source for the one that's chosen. `Resolved` is a closed set in the core, so this kind is added there.
- **Who chooses:** `Resolve` asks a chooser in the `ResolveContext`. The manager supplies one that applies the user's rules, such as "best quality up to 1080p", or asks the user.

### 6.3 Engines (in Naqel.Plugins)

The manager drives every engine through one contract, Naqel's own engine included:

```csharp
public interface ITransferEngine : IAsyncDisposable
{
    EngineCapabilities Capabilities { get; }          // pause, seeding, file selection, priorities, limits per job and overall, uploads
    bool CanHandle(Uri reference);                    // magnet:, .torrent files, metalinks...
    Task StartAsync(CancellationToken cancellationToken);   // start the engine or connect to it, and restore its state
    Task<IEngineJob> AddAsync(JobRequest request, CancellationToken cancellationToken);
    Task<IEngineJob?> AttachAsync(string jobId, CancellationToken cancellationToken);   // after a restart
    IAsyncEnumerable<JobUpdate> WatchAsync(CancellationToken cancellationToken);
    Task SetLimitsAsync(EngineLimits limits, CancellationToken cancellationToken);
}

public interface IEngineJob
{
    string Id { get; }                                // the engine's own id, which the job store keeps
    JobSnapshot Snapshot { get; }                     // state, bytes, rates, files, peers, error
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task RemoveAsync(bool deleteData, CancellationToken cancellationToken);
    Task SetLimitsAsync(JobLimits limits, CancellationToken cancellationToken);
}
```

- **States:** queued, needs attention, getting metadata, checking, transferring, paused, seeding, completed, failed. Errors use `TransferErrorKind`, so every engine's failures read the same.
- **A job is not a transfer:** a torrent is many files and keeps seeding after it completes. The Naqel engine's adapter maps one job to one or more `TransferOperation`s, and its live controls (`SetMaxBytesPerSecond`, `SetMaxConnections`, per-part progress) to the job's.
- **BitTorrent through MonoTorrent:** the first engine besides Naqel's own. MonoTorrent is MIT-licensed and runs in-process on every platform. The contract is fully async and allows engines in another process or container too, so adapters for programs such as qBittorrent could follow without changing it; such programs, under the GPL, would stay separate programs.
- **Where files go:** an engine saves into a storage location the manager gives it. For an engine that can only write to its own folders, a completion step moves the result with a Naqel transfer (section 6.4).

### 6.4 Hooks (in Naqel.Plugins and Naqel.Manager)

Plugins can take part in a job's life without being an engine or a resolver. That keeps the core open for features that come later, which then build on these hooks instead of changing the contracts:

- **Job events:** every change to a job is published, and plugins can subscribe: added, started, a new state, needs attention, completed, failed, removed. Observers run outside the job's own work, so a slow or failing observer never delays or fails a job.
- **Completion steps:** steps that run after a job's content is complete and before the job counts as completed. The manager uses one itself, to move the result of an engine that writes only to its own folders into the job's storage location. A step can fail the job, or add to its data.
- **Job data:** jobs carry labels and plugin data, kept in the job store, that plugins can set and read, so a feature can remember what it did with a job.
- **Operations for plugins:** a plugin can use the operations of `Naqel.Contracts`, such as adding a job or copying a file to a storage location, through the plugin host, the same way the UI does.

### 6.5 Plugins (in Naqel.Plugins)

```csharp
public interface INaqelPlugin
{
    PluginInfo Info { get; }                          // id, name, version, the contract version it was built for
    void Register(IPluginRegistry registry);          // adds resolvers, storages, engines, hooks, account providers, settings
}
```

- **Order:** when several plugins could handle a link, the most specific wins: site plugins first, then formats, then protocols. This is the order the host gives `Resolve`, which already asks resolvers in order.
- **The catalog:** plugins are packages, installed from a catalog of sources such as NuGet feeds. The catalog lists the plugins of its sources, installs, updates and removes them, and offers only versions built for a contract version this Naqel supports.
- **Loading:** each plugin in its own `AssemblyLoadContext`, so plugins can bring their own dependencies, and can be updated or removed without affecting the others. A manifest says what a plugin is and which contract version it needs, so the host can refuse an incompatible plugin before loading it.
- **Settings:** a plugin describes its settings as data, and both UIs render them, so a plugin needs no UI code of its own, and its settings can be changed from the desktop app and the browser alike.
- **Trust:** a plugin runs with Naqel's rights. Only the user installs plugins and engines, from sources the user added, and Naqel never downloads or starts an executable without the user agreeing.

### 6.6 Accounts and asking the user (in Naqel.Plugins)

- **Accounts:** `IAccountProvider` signs in to a service and hands out fresh credentials, such as the token function `BearerTokenHandler` takes. Sign-ins that need a browser, such as OAuth, open it on the desktop, and use a redirect back to the web UI on the server.
- **Asking the user:** a plugin can raise a request instead of failing: a password for a protected link, choosing a format, confirming an overwrite, signing in, or a captcha the user solves themself. It's an event in `Naqel.Contracts`, so whichever UI is connected shows it (section 3). Plugins never solve captchas themselves.

## 7. How a link becomes a job

1. The user adds a link in the UI or through the API, the browser extension hands one over, or a site plugin hands over several. In the desktop app, it goes to this computer or to the connected server.
2. The manager offers it to the engines: an engine that claims it, such as BitTorrent for a `magnet:` link, gets the job.
3. Otherwise the plugins' ordered resolvers resolve it: files become sources, references resolve further, and choices go to the chooser.
4. Each source, with a target in the chosen storage location, becomes a job on the Naqel engine.
5. Jobs can chain: a site offers a `.torrent` file, the Naqel engine downloads it, and the BitTorrent engine takes over. When a job's content is complete, its completion steps run, and observers hear about each change.

## 8. Phases

Each phase is a few topic commits with tests, reviewed before the next starts. The desktop app comes first; the server follows, reusing everything below the UI.

1. **Manager foundations.** `Naqel.Plugins` with the engine contract and the hooks; `Naqel.Manager` with jobs, the queue, routing, job events, completion steps and job data; the Naqel engine adapter. Tested with fake plugins and a fake engine, and driven from a small console host.
2. **Storage.** `IStorage` and the generic storage resolvers in `Naqel.Core`, tested against a fake storage service; storage locations in the manager.
3. **Plugin host.** The catalog with NuGet sources, manifests, a load context per plugin, ordering, plugin settings, accounts and asking the user. The protocol packages install and load as plugins.
4. **Contracts, application and data.** `Naqel.Contracts` with every operation and event; `Naqel.Application` implementing it, with the host interfaces; `Naqel.Data` with EF Core and SQLite. The console host moves onto it.
5. **Desktop.** `Naqel.Presentation` and `Naqel.Desktop`, standalone, with an Avalonia UI on Windows, macOS and Linux: the queue, job details with per-part progress, live speed and connection controls, adding links with format choices, storage locations, settings, accounts and plugins; tray, notifications, link handling and file pickers.
6. **Server.** `Naqel.Server` with the REST API, OpenAPI, SignalR, an optional password and API keys; `Naqel.Client`; a Docker image.
7. **Web UI.** `Naqel.Web` with Vue.js and TypeScript, on a client generated from the OpenAPI description, served by the server.
8. **Desktop connected to a server.** Server connections and the connection switcher in the desktop app, through `Naqel.Client`, with version checks.
9. **BitTorrent.** The MonoTorrent engine, including the chain from a `.torrent` file downloaded over HTTP.
10. **Content with choices.** Content kinds and choices in `Naqel.Core`, `Naqel.Hls`, and a first site plugin that uses them. A podcast feed is a good first one: standard formats, no sign-in.
11. **More protocols and services.** FTP, SFTP, WebDAV, SMB, S3 and tus, more site plugins, and the browser extension.

## 9. Decisions

| Question | Decision |
|---|---|
| Desktop platforms and framework | Windows, macOS and Linux, with Avalonia |
| Server UI | A Vue.js web app, not wrapped in a desktop shell |
| The desktop's own jobs and a server's | One connection at a time, with a switcher; no combined view |
| Users | One user and one setup, on the desktop and the server; a password on the server is optional |
| First engine besides Naqel's own | MonoTorrent, for BitTorrent |
| Plugin distribution | A catalog that installs plugins from sources such as NuGet feeds |
| Plugin isolation | Each plugin in its own `AssemblyLoadContext` |
| Scheduling | Not a feature |
| Features that come later | Built on hooks: job events, completion steps, job data and operations for plugins |
