# Naqel design

This document describes how the Naqel libraries work and why; the app built on them is planned in [architecture.md](architecture.md). In Naqel, any transfer — a download, an upload, or a copy between two servers — is the same operation: bytes move from a **source** to a **target**, and one **engine** does the work that every protocol needs.

## 1. Goals and non-goals

### Goals

1. **One model for both directions.** A download is "HTTP source → file target". An upload is "file source → HTTP target". Nothing in the core knows which one it is running.
2. **Protocols only move bytes.** A protocol package implements reading a range and writing a range. Retries, timeouts, throttling, parallel parts, progress, resume and integrity checks are written once, in the engine.
3. **Many protocols as separate packages.** `Naqel.Core` is the core, and each protocol ships as `Naqel.<Protocol>` (`Naqel.Http` first; `Naqel.Sftp`, `Naqel.Ftp`, `Naqel.S3`, `Naqel.Tus` later). A third party can add a protocol using only the public API.
4. **Idiomatic modern .NET.** `async` all the way, `CancellationToken` everywhere, `IProgress<T>` instead of events, a returned result, exceptions for failures, and options fixed before the transfer starts.
5. **Testable without a network.** In-memory sources and targets (`MemorySource` and `MemoryTarget` in the tests) drive every engine test.
6. **Every supported .NET, one API.** The packages build for net8.0, netstandard2.1 and netstandard2.0 with the same public API (see [7.2](#72-target-frameworks)). The net8.0 build has no dependencies beyond the base library. The API uses `init` setters, records and `with`, so it needs C# 9 or later, also on .NET Framework.

### Non-goals

- Transfers of whole folders or many files in one call (a batch API can come later on top of single transfers).
- A download manager: queues, scheduling and storing transfers in a database. The core provides checkpoints and a store abstraction that such a manager can build on.
- Remote file management (list, delete, rename, create folders).
- Checksums beyond SHA-1, SHA-2 and MD5, and verifying content that already sits at the target without transferring it.

## 2. Design principles

- **The engine owns behavior, protocols own bytes.** Retrying, throttling, splitting into parts, timing out, reporting progress and resuming are the same problem for every protocol, so they live in one place. A protocol never contains a retry loop.
- **Both directions are symmetric.** Sources and targets are the only two roles, so a file can be the target of a download and the source of an upload, and two remote endpoints can be connected directly.
- **Contracts describe capabilities, not protocols.** A target says what it can do (parallel parts, partial parts, resuming, reading back) and the engine plans around it, instead of the core knowing about HTTP, S3 or SFTP.
- **Settings are fixed before a transfer starts.** Options are immutable records, so a running transfer never sees its settings change.
- **Failures say what happened.** Every error has a kind, the side it came from, and whether retrying can help, so both the engine and the caller can react without parsing messages.

## 3. Core concepts

```
                 ┌──────────────────────── engine ────────────────────────┐
 ITransferSource │ plan parts → read range → meter (rate, progress, idle) │ ITransferTarget
 (HTTP, file, …) │ → write part → retry → checkpoint → verify → complete  │ (file, HTTP, …)
                 └────────────────────────────────────────────────────────┘
```

| Concept | Type | Responsibility |
|---|---|---|
| Range | `ByteRange` | A span of bytes: an absolute start plus an optional length |
| Source | `ITransferSource` | Describe the content, and open a stream for a range of it |
| Target | `ITransferTarget`, `ITargetSession` | Accept parts at given ranges, then commit or discard them |
| Engine | `TransferOperation` (run by `TransferClient`) | Plan parts, run them in parallel, retry, throttle, time out, report progress, verify, commit |
| Options | `TransferOptions`, `RetryPolicy` | Immutable settings for one transfer |
| Outcome | `TransferResult`, `TransferException` | What happened |
| Resume | `TransferCheckpoint`, `ICheckpointStore` | Enough state to continue a transfer after the process restarts |
| Integrity | `Checksum` | The hash the content must have |

All core types live in the `Naqel` namespace. File endpoints live in `Naqel.Files`, stream endpoints in `Naqel.Streams`, and each protocol package uses its own namespace (`Naqel.Http`).

## 4. Contracts for protocol implementers

### 4.1 `ByteRange`

```csharp
public readonly struct ByteRange : IEquatable<ByteRange>
{
    public static ByteRange All { get; }          // Start 0, Length null

    public long Start { get; }
    public long? Length { get; }                  // null: to the end of the content
    public long? End { get; }                     // Start + Length - 1, null when open-ended

    public ByteRange(long start, long? length = null);
    public static ByteRange FromBounds(long start, long end);   // inclusive end

    public ByteRange Skip(long count);            // the rest of the range after count bytes
}
```

Ranges are always absolute. HTTP suffix ranges (`bytes=-500`, "the last 500 bytes") aren't part of the core type, because no other protocol has them and the engine needs absolute positions to resume.

### 4.2 `ITransferSource`

```csharp
public interface ITransferSource
{
    /// Describes the content without transferring it (HTTP: a probe request; file: file metadata).
    Task<SourceInfo> GetInfoAsync(CancellationToken cancellationToken);

    /// Opens the content for a range. The returned info describes what this response revealed.
    Task<SourceRead> OpenReadAsync(ReadRequest request, CancellationToken cancellationToken);
}

public sealed class ReadRequest
{
    public ByteRange Range { get; }
    public string? IfVersion { get; }     // fail with SourceChanged if the content is no longer this version
}

public sealed class SourceRead : IDisposable
{
    public Stream Content { get; }
    public SourceInfo Info { get; }
}

public sealed record SourceInfo
{
    public long? Length { get; init; }                 // the whole content; null when unknown (chunked HTTP, a live stream)
    public bool SupportsRanges { get; init; }          // can OpenReadAsync start anywhere but 0?
    public bool IsRepeatable { get; init; } = true;    // can the content be read again after a failure?
    public string? Identity { get; init; }             // what the content is: the requested URL, a file's full path
    public int? MaxConnections { get; init; }          // at most this many connections at once, such as 1 for a free file-host account
    public string? Version { get; init; }              // opaque: an ETag, a last-modified time, a file's size + write time; without one, it's read in one piece
    public string? Name { get; init; }                 // a suggested file name (Content-Disposition, the URL path, the file name)
    public string? ContentType { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public Checksum? Checksum { get; init; }           // the hash the source reports, such as HTTP's Repr-Digest
    public IReadOnlyDictionary<string, string> Metadata { get; init; }   // passed to targets that can store it
}
```

Rules a source must follow:

- When `SupportsRanges` is false, the engine only asks for `ByteRange.All`.
- When `IfVersion` is set and the content has changed, throw `TransferException` with `TransferErrorKind.SourceChanged` instead of returning different bytes. HTTP maps this to `If-Range`.
- `OpenReadAsync` returns when the content is ready to read (HTTP: when the response headers have arrived). The engine applies the response timeout around it.
- The source maps its own errors to `TransferException` (see [errors](#62-errors)).

The engine doesn't call `GetInfoAsync` when it doesn't need to plan: a single-connection transfer opens `ByteRange.All` straight away and reads the info from the `SourceRead`. A simple download therefore costs one request.

### 4.3 `ITransferTarget` and `ITargetSession`

```csharp
public interface ITransferTarget
{
    TargetCapabilities Capabilities { get; }

    /// Starts writing a new transfer, or re-attaches to an unfinished one when ResumeToken is set.
    Task<ITargetSession> OpenAsync(TargetOpenRequest request, CancellationToken cancellationToken);
}

public sealed class TargetOpenRequest
{
    public SourceInfo Source { get; }          // length, name, content type, metadata
    public string? ResumeToken { get; }        // from a checkpoint
    public int BufferSize { get; }             // from TransferOptions.BufferSize, to copy with
}

public interface ITargetSession : IAsyncDisposable
{
    /// Where the content is once completed: a file's full path, an upload's URL; null for a stream. Becomes TransferResult.Location.
    string? Location { get; }

    /// What the target created, such as a file id, a revision or a share link. Becomes TransferResult.TargetMetadata.
    IReadOnlyDictionary<string, string> Metadata { get; }

    /// Identifies the unfinished transfer so it can be continued later (a temp file path, an S3 upload id, a tus URL).
    string? ResumeToken { get; }

    /// Writes one part. content holds exactly range.Length bytes when the length is known.
    Task WritePartAsync(ByteRange range, Stream content, CancellationToken cancellationToken);

    /// Makes the result visible: rename the temp file, complete the S3 multipart upload.
    Task CompleteAsync(CancellationToken cancellationToken);

    /// Discards everything written so far.
    Task AbortAsync(CancellationToken cancellationToken);
}

public interface IAcknowledgingSession
{
    /// Bytes kept for good, without a gap, from this position of the content on: what the server acknowledged.
    long Kept(long position);
}

public interface IVerifiableSession
{
    /// Hashes the first length bytes written so far, to verify a checksum before completing.
    Task<byte[]> ComputeHashAsync(HashAlgorithmName algorithm, long length, CancellationToken cancellationToken);
}

public sealed record TargetCapabilities
{
    public bool ParallelParts { get; init; }      // parts can be written at the same time, in any order
    public bool SplittableParts { get; init; }    // a part's stream may end before its range does (see 5.2.1)
    public bool ResumableParts { get; init; }     // bytes consumed from a failed part's stream are kept
    public bool Resumable { get; init; }          // OpenAsync accepts a ResumeToken; a disposed session keeps its data
    public bool RepeatableWrites { get; init; } = true;  // writing a part again is harmless (files, PUT); false for POST
    public bool RequiresLength { get; init; }     // the total length must be known up front (for example a server that refuses chunked uploads)
    public bool Verifiable { get; init; }         // sessions implement IVerifiableSession
    public long MinPartSize { get; init; }        // S3: 5 MiB except the last part
    public long? MaxPartSize { get; init; }
    public int? MaxParts { get; init; }
    public long? PartSizeMultiple { get; init; }  // parts but the last are multiples of this, such as OneDrive's 320 KiB
}
```

Why the target takes a `Stream` instead of byte buffers: a file target writes what it reads from the stream at an offset, and an HTTP target wraps it in an `HttpContent` and lets `HttpClient` pull from it. Both work without the target knowing about buffers, and the engine controls the stream it hands over (see [the metered stream](#53-the-metered-stream)).

How existing and planned protocols fit:

| Target | ParallelParts | ResumableParts | Resumable | RepeatableWrites | Commit (`CompleteAsync`) |
|---|---|---|---|---|---|
| File (`Naqel.Files`) | yes, writes at offsets | yes | yes, the temp file path | yes | rename `name.naqel-part` to `name` |
| HTTP PUT (`Naqel.Http`) | no, one part | no | no | yes | nothing, the request is the commit |
| HTTP POST, forms (`Naqel.Http`) | no, one part | no | no | no | nothing, the request is the commit |
| tus (`Naqel.Tus`) | no, in order | yes, the server keeps the offset | yes, the upload URL | yes | nothing |
| S3 multipart (`Naqel.S3`) | yes | no, a part is redone | yes, the upload id | yes | `CompleteMultipartUpload` |
| SFTP (`Naqel.Sftp`) | yes, writes at offsets | yes | yes, the remote temp path | yes | rename |
| FTP (`Naqel.Ftp`) | no, one stream | yes, `APPE`/`REST` | yes | yes | rename |

When a target isn't `RepeatableWrites`, the engine only writes a part again if the target failed before reading all of it: a request that never got its whole body can't have been processed, but a `POST` whose answer was lost may have created the upload already. The error stays as it was, transient or not, so a caller who knows the server can take a repeat may try again.

### 4.4 Resources and ownership

- Sources and targets are lightweight descriptions, with no state from a run: what a run produced, such as where the file ended up, comes back in the `TransferResult` (`Location`), so one `FileTarget` can serve many transfers. Stream endpoints are the exception by nature: they wrap one stream the caller owns, so each belongs to one transfer. Expensive resources (an `HttpClient`, an SFTP connection pool) belong to a protocol object the caller creates and disposes, such as `HttpTransport`. Shortcuts like `HttpSource.Get(uri)` use a shared `HttpClient`, so there is nothing to dispose.
- The engine disposes what it opens: every `SourceRead`, and every `ITargetSession` with `DisposeAsync`, since releasing a remote target can involve I/O. Disposing waits at most the response timeout.
- A target that says it's `Verifiable` must return sessions that implement `IVerifiableSession`; otherwise the transfer fails as `Unsupported` as soon as the session opens, instead of skipping the checksum.
- Disposing a session without calling `CompleteAsync` or `AbortAsync` leaves the partial data in place, so the transfer can be resumed.

### 4.5 Resolving references

A reference that arrives as text, from a user, a file or configuration, isn't always the content: a share link, a download page, a shared folder or a playlist stands for one or more sources, and an upload destination such as a folder stands for many targets. Resolvers turn references into endpoints, for any protocol: a reference is any URI, including schemes a package defines, such as `gdrive:` or `dropbox:`.

```csharp
public interface ISourceResolver
{
    bool CanResolve(Uri reference);                                    // a quick look: scheme or host
    IAsyncEnumerable<Resolved> ResolveAsync(Uri reference, ResolveContext context, CancellationToken cancellationToken);
}

public abstract class Resolved                                        // what a reference resolved to: one of the two below
{
    public string Path { get; }                                       // where it belongs: "", "a.jpg", "album/02.mp3"
}

public sealed class ResolvedSource : Resolved                         // a source to transfer
{
    public ResolvedSource(ITransferSource source, string path = "", SourceInfo? info = null);
    public ITransferSource Source { get; }
    public SourceInfo? Info { get; }                                   // what's known before transferring, such as a listing's sizes
}

public sealed class ResolvedReference : Resolved                      // a reference to resolve further
{
    public ResolvedReference(Uri next, string path = "");
    public Uri Next { get; }
}

public interface ITargetResolver
{
    bool CanResolve(Uri destination);
    ValueTask<ITransferTarget> ResolveAsync(Uri destination, string relativePath, SourceInfo? info, ResolveContext context,
        CancellationToken cancellationToken);
}
```

- **Both ends.** With a resolver for each side, any copy is a pairing: every source a reference resolves to goes to the target its relative path resolves to inside the destination. A download, an upload and a copy between two services are the same code.
- **References lead to references.** A short link leads to a download page, which leads to the file, each handled by a different package. A `ResolvedReference` hands the next step on, and what it resolves to is placed under its path. `Resolved` has only these two kinds, so a resolver's output is either a source with a source in it or a reference with a next one, never neither; `Resolve.SourcesAsync` follows every reference and gives only `ResolvedSource`s.
- **Relative paths can't escape.** `RelativePath` defines them: empty for a single item, otherwise segments separated by `/`, never empty, `.` or `..`. A target resolver still makes each segment acceptable to its storage.
- **Streaming.** A resolver yields sources as it finds them, so a large folder starts transferring before it's fully listed.
- **No registry.** `Resolve.SourcesAsync(reference, resolvers)` resolves with the first resolver in the caller's list that handles each reference, follows references through the same list up to `Resolve.DefaultMaxDepth` steps, and fails on a loop. `Resolve.TargetAsync` picks a target resolver the same way. Which resolvers exist, and in what order, is always the caller's choice; registering them is left to an application or a dependency-injection package. Errors name a reference without its user name, password or query, which can hold credentials.
- **Files.** `FileSourceResolver` resolves a `file:` reference to a file into its source, and a folder into all its files with their paths, leaving out unfinished transfers and checkpoints. `FileTargetResolver` writes a relative path inside a `file:` destination folder, making each segment safe like `FileTarget.InDirectory`; a destination that isn't a folder is the file a single item goes to.
- **Not for typed inputs.** A package whose SDK already returns typed objects, such as a file's metadata, offers direct factories for them; resolving is for references that arrive as text.
- **Choices per resolution.** A `ResolveContext` goes to every resolver along the way. It carries typed options, each package defining its own options class and reading it with `context.Get<T>()`, such as a video's quality, which account to use, or `FileResolveOptions` for the file resolvers; a resolver ignores options it doesn't know. Its `Filter` keeps only some sources, and `Resolve.SourcesAsync` applies it once each path is final, so a filter on `art/` sees `art/cover.jpg`, not the `cover.jpg` its resolver gave. A context never changes: `With<T>` and `WithFilter` return copies.

**Pitfalls for resolver authors.** References are `Uri`s, and `Uri` treats some of them in ways a package has to plan for. Tests check each of these on every runtime:

- **Use schemes of two or more letters.** On Windows, a one-letter scheme is read as a drive letter: `x:1` doesn't parse, and `x:/1` is a file.
- **Keep case-sensitive identifiers out of the host.** `Uri` lowercases the scheme and the host: `gdrive://AbcXyz` has the host `abcxyz`. An opaque reference such as `gdrive:AbcXyz`, or a path such as `dropbox:/Folder/AbcXyz`, keeps its case.
- **Unescape what you read.** `AbsolutePath` comes back escaped: `dropbox:/My Photos/a.jpg` gives `/My%20Photos/a.jpg`. Split the path at `/` first, then unescape each segment with `Uri.UnescapeDataString`.
- **Build file references from paths.** `new Uri(path)` for a local path keeps characters such as `#`, `%` and spaces in the file name, and `LocalPath` gives the path back.
- **References are compared as `Uri` normalizes them.** `Resolve` finds loops by `AbsoluteUri`, so references that differ only in the case of their scheme or host count as the same reference.

### 4.6 Storages

A storage is a place where files live and can be browsed: a local folder, a network share, an SFTP server, a cloud drive; one account on one service. Where a resolver only answers "what does this reference stand for", a storage can also list, describe and change what's there, which a manager needs to browse remote folders and to save into them. It lives in the `Naqel.Storage` namespace:

```csharp
public interface IStorage
{
    StorageCapabilities Capabilities { get; }                          // ignores case, has ids, allows duplicate names
    Task<StorageItem?> GetAsync(string path, CancellationToken cancellationToken);
    IAsyncEnumerable<StorageItem> ListAsync(string folder, bool recursive, CancellationToken cancellationToken);
    ITransferSource OpenRead(StorageItem file);
    ITransferTarget OpenWrite(string path, WriteMode mode, SourceInfo? info);   // overwrite, fail or keep both
}

public sealed record StorageItem(string Path, string Name, bool IsFolder)
{
    public string? Id { get; init; }                                   // the storage's own id, which survives a move
    public long? Size { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string? Version { get; init; }                              // such as an ETag or a revision
    public IReadOnlyDictionary<string, string> Hashes { get; init; }   // by the storage's own name for each
}
```

- **Paths** are relative to the storage's root, in the form `RelativePath` describes, so they can't leave it.
- **Optional operations** are interfaces of their own, so a storage implements only what it can: `IStorageDelete`, `IStorageMove`, `IStorageCopy` (a copy that doesn't move bytes through the app) and `IStorageLookup` (finding an item by id).
- **One resolver pair for every storage.** `StorageSourceResolver` and `StorageTargetResolver` map the references under a root URI, such as `dropbox:/` or `location://media/`, to paths in a storage, so a storage package implements only listing, describing, reading and writing. A folder resolves to its files with their paths inside it, and a single file goes into a destination folder under its own name. Their targets are `StorageTarget`s, which say which storage and path they write, so what a job wrote can be found again, such as to delete it.
- **Google Drive's model.** Items have ids as well as paths, because some services identify files by id and allow two files with the same name in one folder. A path then doesn't always name one item; `GetAsync` returns one of them, `IStorageLookup` finds each by id, and `StorageSourceResolver` numbers the later ones of a folder, as in `a (2).txt`, so each keeps a place of its own wherever the folder goes.
- **Local folders.** `FileStorage` in `Naqel.Files` is a local folder as a storage, with delete, move and copy. It ignores case on Windows and macOS, keeps it on Linux, leaves out unfinished transfers and checkpoints, and writes names that are safe on every OS.

## 5. The engine

### 5.1 How a transfer runs

1. **Plan.** If the transfer can use more than one connection, or resumes from a checkpoint, call `source.GetInfoAsync`. Split the content into parts (see [5.2](#52-planning-parts)). Otherwise use one part covering `ByteRange.All`.
2. **Open the target.** `target.OpenAsync` with the source info and, when resuming, the checkpoint's resume token, within the response timeout and with its own retries. With one connection this happens once the first read has described the content, but in the same way: a target that fails to open doesn't make the engine read the source again. A target that `RequiresLength` fails early with `Unsupported` when the length is unknown.
3. **Run the parts** with at most `MaxConnections` running at once. For each part:
   1. `source.OpenReadAsync(range, IfVersion = info.Version)`, within the response timeout.
   2. Wrap the content in the metered stream.
   3. `session.WritePartAsync(range, meteredStream)`.
   4. On success, mark the part finished, which the next checkpoint reflects.
   5. On a transient failure, wait according to the retry policy and try again. When the target has `ResumableParts` and the source `SupportsRanges`, continue after the bytes the target already consumed; otherwise redo the whole part.
4. **Verify.** When the length is known, the bytes transferred must equal it, or the transfer fails with `Integrity`. When there is a checksum to check, it's checked now, before the target makes the result visible (see [6.5](#65-checksums)).
5. **Commit.** `session.CompleteAsync`, then return a `TransferResult`.

Inside the engine, three types share this work: `TransferRun` runs steps 1, 2, 4 and 5, restarts when the content changed, owns the target session and records the outcome; `PartRunner` runs step 3 and gives the rest of the run the same retries and timeouts; `RunState` holds what the run knows (the content, the parts, the counters progress and the result read), builds checkpoints and decides whether one can be continued.

Failures:

- A part that fails with a non-transient error, or runs out of retries, cancels the other parts and fails the transfer.
- `SourceChanged` restarts the whole transfer once from zero (the bytes already written belong to an old version), then fails if it happens again.
- On failure or cancellation the engine disposes the session without aborting it, so the partial data stays for a resume. `TransferOptions.DiscardPartialOnFailure` calls `AbortAsync` instead. Targets that aren't `Resumable` are always aborted.
- Exceptions thrown by the caller's own code (an `IProgress<T>` implementation, a callback) are never retried and surface unchanged.

### 5.2 Planning parts

The planner is a pure function, tested on its own:

```
parts = Plan(source length, source.SupportsRanges, target capabilities, options)
```

- One part when the length is unknown, the source doesn't support ranges, the target doesn't take `ParallelParts`, `MaxConnections` is 1 (the smaller of `TransferOptions.MaxConnections` and the source's `SourceInfo.MaxConnections`), or the content is smaller than `MinSplitPartSize × 2`.
- Otherwise `min(MaxConnections, length / MinSplitPartSize)` parts of equal size, adjusted to the target's `MinPartSize`, `MaxPartSize` and `MaxParts`, and rounded up to its `PartSizeMultiple`; when that would make parts larger than `MaxPartSize`, they are the largest multiple that fits, and only the last part may be shorter.
- When resuming, finished parts from the checkpoint are skipped, and partly finished ones continue from their recorded offset.
- Content without a `Version` is never assembled from separate reads, because they could come from different versions of the content. The engine uses one part, starts a failed part over instead of continuing it, and makes no checkpoint. When a checksum will be verified (`ExpectedChecksum`), a splice would be caught, so parallel parts and continuing a failed part stay allowed; a checkpoint still needs a version, since a later run can't check the content otherwise.

### 5.2.1 Splitting running parts

Planned parts of equal size rarely finish together: one connection is slower, or a part retried. The connection that freed up then sits idle while the slowest part holds up the end of the transfer. So when a connection frees up, or a part starts while one is free, and no part is waiting for it, the engine gives the end of the running part with the most bytes left to a new part. `TransferOptions.AdaptiveSplitting` turns this on, and is on by default; turned off, a transfer keeps exactly the planned parts:

- **Where:** halfway through what that part hasn't read yet, at a multiple of the target's `PartSizeMultiple`, and only when both halves keep at least `MinSplitPartSize` bytes and the new part the target's `MinPartSize`, and the target takes another part (`MaxParts`).
- **How:** the running part's stream ends sooner. The engine never gives away bytes the part already read, or that a read in flight may still return, so no byte moves twice and nothing is lost. The target's write sees its content end before the range it was given; a target declares `SplittableParts` when that is fine, which is when it writes each byte where it belongs as it arrives, as files and seekable streams do.
- **When not:** the length is unknown, the source can't read ranges or has no version to check the new part's read against (content without a `Version` is never combined from separate reads), the session acknowledges what it keeps (an upload session's chunks were declared already), a single part is hashed as it passes for a checksum, or the source's `MaxConnections` is reached.
- The new part follows the one it came from, so checkpoints still list the parts in order, and a checkpoint or progress report never sees a part half split. `TransferResult.Parts` counts the parts that were split off.

Callers don't check range support or the size themselves before choosing how to transfer; the planner decides. `TransferResult.SingleConnectionReason` says what it decided when it used one part: `NotRequested`, `UnknownLength`, `NoRangeSupport`, `NoVersion`, `ChecksumHashedWhileStreaming`, `TargetTakesOnePart`, `TooSmall`, `ContinuedCheckpoint` or `SourceLimit`; the transfer's trace carries it as `naqel.single_connection_reason`. A required checksum turning off parallel parts is therefore visible instead of silent.

### 5.3 The metered stream

The engine wraps every source stream in an internal `MeteredStream`, a read-only stream that:

- **throttles** each read through the transfer's `BandwidthLimiter`, shared by all its parts, so the limit applies to the whole transfer;
- **counts** bytes for progress, for resuming a part and for checkpoints;
- **stops at the end of the part**, even when the source has more, and fails with a transient `Network` error when the source ends before it;
- **records which side failed**: an exception from reading the source marks a source failure, anything else the target threw is a target failure. Only a source failure lets a `ResumableParts` target continue the part.

A watchdog next to it enforces the **idle timeout**: when no bytes move for `IdleTimeout`, it cancels the attempt and disposes the source stream (which also ends reads that ignore cancellation). The failure is a transient `Timeout` on the side that stalled: the source when the target was waiting for a read, the target otherwise. Time spent waiting for the speed limit doesn't count as idle, and neither does the time after the target has read the whole part: then it's finishing the part, for example waiting for the server's answer to an upload, and `ResponseTimeout` applies instead, as a transient `Timeout` on the target side. `ResponseTimeout` also covers `GetInfoAsync` and `OpenReadAsync` the same way, even when the source ignores cancellation, and the target's `OpenAsync`, `CompleteAsync` and `AbortAsync`, so neither side can hang a transfer, not even while it cleans up after a failure. Only reading the target back for a checksum has no time limit, because it takes as long as the content is large; it can still be canceled.

Throttling, progress and timeouts are therefore the same for every protocol and both directions, and no protocol contains that code.

`BandwidthLimiter` is a small public token bucket. Passing the same instance to several transfers (or setting it on a `TransferClient`) limits their combined rate. Its `BytesPerSecond` can change at any time, and 0 means no limit.

### 5.3.1 Changing limits while a transfer runs

A download manager lets people change the speed limit or the number of connections of a transfer that is already running, so `TransferOperation` has `SetMaxBytesPerSecond` and `SetMaxConnections`, which work before and during a run, and `MaxBytesPerSecond` and `MaxConnections` to read the current values. `TransferOptions` stays immutable: it is what the transfer started with.

- **The speed limit** is the transfer's own `BandwidthLimiter`, which every operation has, set to 0 when there is no limit. A new rate applies to the next read; a read already waiting finishes its wait. A shared limiter still applies on top.
- **The connections** go through an internal gate the parts wait at, whose limit can change. Raising it starts waiting parts at once. Lowering it stops no running part, which would throw away the bytes it is moving and its connection; it starts no new parts until fewer run. The source's `SourceInfo.MaxConnections` still caps it.
- More connections than parts are filled by splitting running parts (see [5.2.1](#521-splitting-running-parts)), so raising the connections of a transfer that started with one connection speeds it up too, when the target and the source allow it. With `AdaptiveSplitting` off, raising the connections only starts parts that are waiting.

### 5.4 Progress

```csharp
public sealed class TransferProgress
{
    public TransferState State { get; }          // Preparing, Transferring, Retrying, Completing, Completed, Failed, Canceled
    public long Transferred { get; }
    public long? Total { get; }
    public double? Fraction { get; }             // 0..1, null when the total is unknown
    public double CurrentRate { get; }           // bytes per second over the last few seconds
    public double AverageRate { get; }
    public TimeSpan Elapsed { get; }
    public TimeSpan? Remaining { get; }
    public int ActiveParts { get; }
    public int CompletedParts { get; }
    public int TotalParts { get; }
    public Exception? LastError { get; }         // set while Retrying, and to the failure when Failed
    public IReadOnlyList<PartProgress> Parts { get; } // every part: its range, bytes moved, and Waiting, Running, Retrying, Done or Failed
}
```

Progress is reported from a timer every `ProgressInterval` (default 100 ms), and once more when the transfer ends, however it ends: `Completed`, `Failed` (with the error in `LastError`) or `Canceled`; never on every buffer read. A handler that throws on the final report of a failed transfer can't replace the transfer's error. The engine calls `IProgress<T>.Report` directly. It doesn't marshal to a UI thread; `Progress<T>` already does that when the caller wants it. Because reports come from the timer, the caller's handler never runs inside a read: when it throws, the transfer stops and `RunAsync` throws that exception unchanged, without retrying.

## 6. The public API

### 6.1 Running a transfer

```csharp
// Download with progress
var result = await Transfer
    .From(HttpSource.Get("https://example.com/file.zip"))
    .To(FileTarget.InDirectory("downloads"))          // the name comes from SourceInfo.Name
    .RunAsync(progress, cancellationToken);

// Parallel download with a speed limit
await Transfer
    .From(HttpSource.Get(uri))
    .To(FileTarget.At("file.zip"))
    .WithMaxConnections(4)
    .WithMaxBytesPerSecond(1024 * 1024)
    .RunAsync();

// Upload
await Transfer
    .From(FileSource.At("backup.tar"))
    .To(HttpTarget.Put(uploadUri))
    .RunAsync();

// Copy between servers without touching the disk, with a future Naqel.Sftp package
await Transfer.From(HttpSource.Get(uri)).To(SftpTarget.At(connection, "/backups/file.zip")).RunAsync();
```

The types behind it:

```csharp
public static class Transfer
{
    public static TransferBuilder From(ITransferSource source);     // uses TransferClient.Default
}

public sealed class TransferBuilder
{
    public TransferBuilder To(ITransferTarget target);
    public TransferBuilder WithOptions(TransferOptions options);
    public TransferBuilder WithMaxConnections(int value);
    public TransferBuilder WithMaxBytesPerSecond(long bytesPerSecond);
    public TransferBuilder WithRetry(RetryPolicy policy);
    public TransferBuilder WithExpectedChecksum(Checksum checksum);
    public TransferBuilder WithCheckpointStore(ICheckpointStore store);
    public TransferBuilder ResumeFrom(TransferCheckpoint checkpoint);

    public TransferOperation Build();
    public Task<TransferResult> RunAsync(IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class TransferOperation
{
    public Task<TransferResult> RunAsync(IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    public TransferCheckpoint? GetCheckpoint();       // also while running; null when it can't be resumed
    public void SetMaxBytesPerSecond(long bytesPerSecond);   // also while running; 0: unlimited
    public void SetMaxConnections(int maxConnections);       // also while running (see 5.3.1)
}

public sealed class TransferClient
{
    public static TransferClient Default { get; }
    public TransferClient(TransferOptions? defaults = null, BandwidthLimiter? sharedLimiter = null);

    public TransferBuilder From(ITransferSource source);
}
```

`TransferClient` is what an application registers for dependency injection. It holds default options and an optional limiter shared by all its transfers. `Transfer.From` is a shortcut for `TransferClient.Default.From`. There is one way to describe a transfer, the builder, so every transfer can use every feature, such as checkpoint stores. Builder methods are named after the option they set (`WithMaxBytesPerSecond` sets `MaxBytesPerSecond`).

Endpoints are created with static factories named for what they point at: `FileSource.At`, `FileTarget.At` and `InDirectory`, `StreamSource.From`, `StreamTarget.To`, `HttpSource.Get`, `HttpTarget.Put`, `Post` and `PostForm`. Objects that hold settings rather than point at content, such as `HttpTransport` and `TransferClient`, have constructors.

```csharp
public sealed record TransferOptions               // derive variants with `with { … }`; setters validate
{
    public int MaxConnections { get; init; } = 1;
    public long MaxBytesPerSecond { get; init; }             // 0: unlimited
    public RetryPolicy Retry { get; init; } = RetryPolicy.Default;
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int BufferSize { get; init; } = 81920;           // per read from the source, and the buffer targets copy with
    public long MinSplitPartSize { get; init; } = 1024 * 1024;   // a preference; a target's MinPartSize is a limit
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(2);
    public bool DiscardPartialOnFailure { get; init; }
    public bool AdaptiveSplitting { get; init; } = true;     // split running parts for connections that free up (5.2.1)
    public Checksum? ExpectedChecksum { get; init; }
}

public sealed record RetryPolicy
{
    public static RetryPolicy Default { get; }               // 3 retries, 1 s doubling to 30 s, with jitter
    public static RetryPolicy None { get; }
    public int MaxRetries { get; init; }
    public TimeSpan InitialDelay { get; init; }
    public TimeSpan MaxDelay { get; init; }
    public double BackoffFactor { get; init; }
    public bool Jitter { get; init; } = true;                // ±20%
    public TimeSpan GetDelay(int retry, TimeSpan? retryAfter = null);
}
```

The defaults retry transient errors three times with backoff, use an 80 KB buffer (the size `Stream.CopyToAsync` uses), and fail a stalled connection after 30 seconds.

### 6.2 Errors

```csharp
// What protocols throw and the engine produces. Immutable: nothing changes it after it's thrown.
public class TransferException : Exception
{
    public TransferException(string message, TransferErrorKind kind, bool isTransient = false,
        TransferSide side = TransferSide.Unknown, TimeSpan? retryDelay = null, Exception? innerException = null);

    public TransferErrorKind Kind { get; }
    public bool IsTransient { get; }
    public TransferSide Side { get; }                 // Source, Target, Engine, or Unknown when the thrower didn't say
    public TimeSpan? RetryDelay { get; }              // the other side asked to wait this long (HTTP Retry-After, S3 SlowDown…)
}

// What RunAsync throws when a transfer fails, so one type is always enough to catch.
public sealed class TransferFailedException : TransferException   // Kind, IsTransient and RetryDelay come from Error
{
    public TransferException Error { get; }                  // the failure that ended the transfer, also InnerException
    public IReadOnlyList<PartFailure> PartFailures { get; }  // every part that failed, in order; empty outside parts
}

public sealed class PartFailure
{
    public ByteRange Part { get; }
    public TransferException Error { get; }
    public TransferSide Side { get; }                 // the side the engine saw the failure on
    public int Attempts { get; }
}

public enum TransferErrorKind
{
    Unknown, NotFound, AccessDenied, Network, Timeout, Protocol,
    SourceChanged, Integrity, InsufficientStorage, AlreadyExists, Unsupported,
    Unauthorized,    // sign in again or refresh a token
    RateLimited,     // transient: slow down, for RetryDelay when known
    QuotaExceeded,   // a quota or traffic limit is used up until it resets
    Expired          // a link, token or session ran out: resolve the reference again
}
```

- **Protocol details live in the protocol package.** `Naqel.Http` throws `HttpTransferException`, with the `StatusCode` (`null` when no response arrived), `ReasonPhrase`, `Method` and `RequestUri`, and fills `RetryDelay` from `Retry-After`. It maps 404 and 410 to `NotFound`, 401 and 407 to `Unauthorized`, 403 to `AccessDenied`, 429 to a transient `RateLimited`, 408 and 5xx to transient errors, 413 and 507 to `InsufficientStorage`, and other unexpected statuses to `Protocol`.
- **Kinds that need the site to tell.** `QuotaExceeded` and `Expired` can't be read from a status code alone: a 403 can mean "no access", "quota used up" or "signed link expired", depending on the service. Packages for a particular service map them from its error details.
- **Nothing is changed after it's thrown.** The engine records what it knows about a failure, the part and the side it was calling, in `PartFailure` and `TransferFailedException.Side`, and leaves the protocol's exception as it was. A source's own exception therefore reaches the caller unchanged as `Error`.
- **One type to catch.** `RunAsync` throws `TransferFailedException` for every failure, `OperationCanceledException` for cancellation, and exceptions from the caller's own code (such as a progress handler) unchanged. Protocol details are one pattern away:

  ```csharp
  catch (TransferFailedException ex) when (ex.Error is HttpTransferException { StatusCode: HttpStatusCode.NotFound })
  ```

- **Other exceptions are classified.** `IOException` becomes a transient `Network` error and `TimeoutException` a transient `Timeout`, so a simple protocol doesn't need to map everything. A `TaskCanceledException` the caller didn't cause is HttpClient's own timeout and becomes a transient `Timeout`; any other cancellation the caller didn't ask for is a non-transient `Unknown` error, because retrying a bug doesn't help.
- `SourceChanged` is never retried within a part, whatever `IsTransient` says; the engine starts the whole transfer over instead (see [5.1](#51-how-a-transfer-runs)).

### 6.3 Resuming after a restart

```csharp
var operation = Transfer.From(source).To(target).WithMaxConnections(4).Build();
try
{
    await operation.RunAsync(progress, ct);
}
catch (OperationCanceledException)
{
    var checkpoint = operation.GetCheckpoint();
    File.WriteAllText("file.zip.naqel", checkpoint!.Serialize());
}

// Later, possibly in another process
var checkpoint = TransferCheckpoint.Deserialize(File.ReadAllText("file.zip.naqel"));
await Transfer.From(source).To(target).ResumeFrom(checkpoint).RunAsync();
```

`TransferCheckpoint` holds a format version, a fingerprint of the source's identity, the source version and length, the finished parts and the offset reached in unfinished ones, and the target's resume token. Resuming checks that the source is still the same content in the same version, or starts over. `Serialize` writes a small versioned text format with no JSON dependency; a reader only accepts its own format version, so a checkpoint from an older format starts the transfer over.

How checkpoints behave:

- **Only kept bytes count.** A part counts the bytes the target has handled: the target asks for more only after it wrote what it read before. A checkpoint can therefore lag by one read, which a resume sends again; it never claims bytes the target doesn't have. A target without `ResumableParts` only counts finished parts.
- **Acknowledged bytes, for upload sessions.** A session that keeps only what its server acknowledged, such as a chunked upload to Drive or OneDrive, implements `IAcknowledgingSession`. The engine then asks it what it kept instead of counting what it read:
  - A failed part continues from the last acknowledged byte, even when the session itself failed, so a dropped connection costs one chunk.
  - Checkpoints count only acknowledged bytes.
  - A session reopened from a checkpoint asks its server where it stands before `OpenAsync` returns, so parts continue from what the server really has, whether it got further after the checkpoint was saved or lost some of it.
  - A part that has to go back behind what the session acknowledged, because the checksum is hashed as the bytes pass or because the part can't continue, needs a new session, since the server won't take those bytes again. The engine then starts the transfer over with one, once, as it does when the content changes.
- **Available while running.** `GetCheckpoint` is safe to call during the transfer, for example from the progress handler, so an app can save it regularly in case the process ends without a chance to save it.
- **`null` when nothing can be continued:** before the transfer starts, after it completes, when the target isn't `Resumable` or discarded its partial data, and when the source has no version or doesn't support ranges.
- **Resuming checks first.** It always asks the source for its info. When the identity, version or length differ, the target can't resume, or it reports `NotFound` for the resume token (the partial file is gone), the transfer starts over as if there were no checkpoint. Otherwise it reopens the target with the token, skips finished parts and continues the others where they stopped, keeping the checkpoint's parts whatever `MaxConnections` is now; connections the checkpoint's parts leave free are filled by splitting them (see [5.2.1](#521-splitting-running-parts)).
- **Identity, not just version.** `SourceInfo.Identity` (the requested URL, a file's full path) tells which content a checkpoint belongs to, so a checkpoint saved for one URL can't continue a download of another that happens to have the same ETag and length. The checkpoint keeps its SHA-256 (`TransferCheckpoint.Fingerprint`), so URLs with credentials or signatures aren't written to disk.
- **Checked when created.** The parts must cover the content exactly once, in order and without gaps, or the constructor throws (and `Deserialize` throws `FormatException`): a damaged or hand-edited checkpoint can't complete a transfer with bytes missing.
- **A target checks its token.** `FileTarget` only continues a partial file it could have written: the file's own for `FileTarget.At`, or one in its directory for `InDirectory` and `FileConflict.KeepBoth`. Any other path counts as `NotFound`, so the transfer starts over instead of writing somewhere else.
- **`TransferResult.Resumed`** tells how many bytes came from the earlier run; `Transferred` counts them too, and `AverageRate` covers only this run.

**Automatic checkpoints.** `WithCheckpointStore(ICheckpointStore)` lets the engine handle all of this:

```csharp
public interface ICheckpointStore
{
    Task<TransferCheckpoint?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(TransferCheckpoint checkpoint, CancellationToken cancellationToken);
    Task DeleteAsync(CancellationToken cancellationToken);
}

await Transfer.From(source).To(FileTarget.At("file.zip")).WithCheckpointStore(FileCheckpointStore.For("file.zip")).RunAsync();
```

- Before starting, the engine loads the stored checkpoint and resumes from it; a checkpoint passed to `ResumeFrom` wins over it.
- While running, it saves the checkpoint every `CheckpointInterval` (2 seconds by default) when it changed, so even a killed process loses at most that much progress. It saves once more when the transfer fails or is canceled.
- When the transfer completes, or stops in a way that can't be continued, it deletes the checkpoint.
- The store is best effort: a failing store never fails a transfer that is otherwise fine. If it can't load, the transfer starts over, which costs progress but no data; if it can't save, a later run may have to start over. Every call to the store waits at most the response timeout, and failures show up as a `naqel.checkpoint.error` event.
- `FileCheckpointStore` writes a temporary file and renames it over the previous checkpoint, so a crash while saving never leaves a damaged checkpoint; an unreadable file counts as no checkpoint. `FileCheckpointStore.For(path)` keeps it next to the file as `path.naqel-checkpoint`.

The store is a separate abstraction rather than a `FileTarget` feature so it works for every target, and for `FileTarget.InDirectory`, whose file name is only known once the transfer starts: the checkpoint's resume token already holds the partial file's path, so the store only needs a key the caller chooses, such as the URL.

### 6.4 Adding a protocol

A complete read-only protocol is a single class. A minimal version of `MemorySource` from the tests:

```csharp
public sealed class MemorySource(byte[] content) : ITransferSource
{
    private SourceInfo Info => new() { Length = content.Length, SupportsRanges = true };

    public Task<SourceInfo> GetInfoAsync(CancellationToken ct) => Task.FromResult(Info);

    public Task<SourceRead> OpenReadAsync(ReadRequest request, CancellationToken ct)
    {
        var range = request.Range;
        var length = range.Length ?? content.Length - range.Start;
        var stream = new MemoryStream(content, (int)range.Start, (int)length, writable: false);
        return Task.FromResult(new SourceRead(stream, Info));
    }
}
```

It gets retries, parallel parts, throttling, progress, checkpoints and checksum verification without any more code.

**A package for a service on HTTP**, such as Dropbox, Google Drive or a file host, builds on `Naqel.Http` instead of starting over. A sketch of an upload through Dropbox's upload sessions:

```csharp
// Tokens come from the package's sign-in; the handler adds them and refreshes them on a 401
var client = new HttpClient(new BearerTokenHandler(account.GetTokenAsync)) { Timeout = Timeout.InfiniteTimeSpan };
var transport = new HttpTransport(client);

public sealed class DropboxTarget(HttpTransport transport, string path) : ChunkedUploadTarget(transport, chunkSize: 8 * 1024 * 1024)
{
    protected override async Task StartAsync(ChunkedUpload upload, CancellationToken ct)
        => upload.Token = await PostJsonAsync("upload_session/start", ct);            // the session id is the resume token

    protected override async Task<long> SendChunkAsync(ChunkedUpload upload, UploadChunk chunk, CancellationToken ct)
    {
        await AppendAsync(upload.Token!, chunk.Offset, chunk.Content, ct);           // upload_session/append_v2
        return chunk.Offset + chunk.Count;
    }

    protected override Task<long> QueryAsync(ChunkedUpload upload, CancellationToken ct)
        => OffsetFromErrorAsync(upload.Token!, ct);                                  // Dropbox reports the offset in an error

    protected override async Task CompleteAsync(ChunkedUpload upload, CancellationToken ct)
        => upload.Metadata["id"] = await FinishAsync(upload.Token!, path, upload.Acknowledged, ct);   // upload_session/finish
}
```

The pieces it uses:

- **`HttpTransport.SendAsync`** sends the package's own API calls with the transport's settings, and **`HttpTransferException.FromResponse`** turns an unsuccessful answer into an error the engine understands, or into the kind the package knows its service means, such as `QuotaExceeded` behind a 403.
- **`HttpSourceOptions`** gives a download a stable `Identity` (such as `dropbox:id:abc`, so a checkpoint fits a later, different signed link), a `Name`, and a `Refresh` that gets a new link when one expired.
- **`BearerTokenHandler`** adds OAuth tokens and refreshes them once on a 401: a request without a body, or with one in memory such as an API call's JSON, is sent again at once, and a streamed upload fails as a transient `Unauthorized`, so the transfer's retry sends it with the fresh token.
- **`ChunkedUploadTarget`** sends the content in chunks, in order, and sends again what the server didn't take. It's an `IAcknowledgingSession`, so a failed transfer, or a later one from a checkpoint, continues from what the server acknowledged; a session the service forgot starts the upload over. `ChunkedUpload.Acknowledged` tells the steps how far the server is, such as the length a finish step needs. Content without a version can't continue a session after a failure, since its bytes might differ from what the server has; it starts a new one.
- **Resolvers.** The package's `ISourceResolver` turns its share links into sources, or into further references; `HttpSourceResolver` resolves plain links, and goes last. `HttpTargetResolver` uploads into WebDAV-like folder URLs.

### 6.5 Checksums

A transfer can be verified against a `Checksum` (MD5, SHA-1 or SHA-2): the caller's (`TransferOptions.ExpectedChecksum`, `WithExpectedChecksum(Checksum.Sha256("…"))`), or else the source's (`SourceInfo.Checksum`; `HttpSource` reads `Repr-Digest` from RFC 9530 and the older `Digest`, preferring SHA-512 over SHA-256).

- **One connection from the first byte** is hashed as the bytes pass, at no extra cost. A part that continues after a source failure keeps its hash, because it continues right after the hashed bytes; a part that starts over starts its hash over.
- **Parallel parts and resumed transfers** can't be hashed while streaming, because a hash can't be combined from pieces. A target with `TargetCapabilities.Verifiable` reads its data back instead, through `IVerifiableSession.ComputeHashAsync`; `FileTarget` and readable seekable `StreamTarget`s do.
- **A required checksum and a target that can't read back** make the engine use one connection and start over instead of resuming, so it can always hash while streaming. The transfer never fails just because the checksum can't be checked.
- **A checksum only the source reported** is checked when that's free, and skipped when it would need a read-back nobody asked for.
- **A mismatch** fails with a non-transient `Integrity` error, and the engine aborts the session, so wrong data never gets the real name and no checkpoint points to it. `TransferResult.VerifiedChecksum` tells which checksum a successful transfer was checked against.

### 6.6 Diagnostics

Transfers report traces and metrics through .NET's own `ActivitySource` and `Meter`, both named `NaqelDiagnostics.ActivitySourceName` (`"Naqel"`). That needs no package, costs next to nothing without a listener, and works with OpenTelemetry, Application Insights, `dotnet-trace` and `dotnet-counters`.

- **Traces:** a `naqel.transfer` activity per run, tagged with the source and target types, `naqel.outcome` (`completed`, `failed` or `canceled`), bytes, resumed bytes, parts and retries, and on failure `error.type` (the `TransferErrorKind`) and `naqel.error.side`. Each part is a `naqel.part` child. Retries are `naqel.retry` events on the part, or on the transfer for describing the content and opening or completing the target; starting over because the content changed is a `naqel.restart` event, and a failing checkpoint store a `naqel.checkpoint.error` event.
- **Metrics:** `naqel.transfer.bytes`, `naqel.transfer.retries` (by error kind and side), `naqel.transfers` and `naqel.transfer.duration` (by outcome and error kind) and `naqel.transfers.active`, all tagged with the source and target types.

There's no `ILogger` in the core: it would add a package dependency, which section 7.1 keeps out of the core, and progress already carries the last error. Integration with `Microsoft.Extensions.Logging` or dependency injection can come later as a separate `Naqel.Extensions` package.

## 7. Packages

| Package | Contents | Dependencies |
|---|---|---|
| `Naqel.Core` | The contracts, the engine, storages (`Naqel.Storage`, with `FileStorage` in `Naqel.Files`), options, errors, checkpoints, checksums, `BandwidthLimiter`, `Naqel.Files` (`FileSource`, `FileTarget`, `FileCheckpointStore`, `FileSourceResolver`, `FileTargetResolver`) and `Naqel.Streams` (`StreamSource`, `StreamTarget`) | none on net8.0; see [7.2](#72-target-frameworks) |
| `Naqel.Http` | `HttpSource` (GET with ranges, probing, `If-Range`, the name from `Content-Disposition`, the checksum from `Repr-Digest`, `Accept-Encoding: identity` on every request so the bytes are the content's, and an `Unsupported` error for a response encoded anyway), `HttpTarget` (PUT, POST or a multipart form in one request), `HttpTransport` (the `HttpClient`, headers, a request configurator for authentication), and for packages for particular services `HttpSourceOptions`, `BearerTokenHandler`, `ChunkedUploadTarget`, `HttpSourceResolver` and `HttpTargetResolver` | `Naqel` |
| Later | `Naqel.Tus`, `Naqel.Sftp`, `Naqel.Ftp`, `Naqel.S3`, `Naqel.AzureBlob` | `Naqel.Core` and that protocol's client library |

The core package is `Naqel.Core`, but its namespace is `Naqel`, so code reads `using Naqel;` next to `Naqel.Files` and `Naqel.Http`, the way `Polly.Core` uses `Polly`. The name `Naqel` alone belongs to the manager app built on the libraries, whose own namespace is `Naqel.App`, so its types never mix with the library's.

### 7.1 Which endpoints belong in the core

An endpoint ships in the core package when **all** of these hold, and in its own `Naqel.<Name>` package otherwise:

1. **It's local I/O that most transfers touch.** A download usually ends in a file or a stream, and an upload usually starts from one. Keeping them in the core means the most common transfer needs one protocol package, not two.
2. **It needs nothing beyond the base library.** Files and streams only use `System.IO`, which every runtime has, so a separate package wouldn't spare anyone a download.
3. **It has nothing protocol-specific to configure.** There's no client, connection, authentication or error mapping of its own to version separately.
4. **It's small and stable**, and can be released together with the engine.

Each endpoint group gets its own namespace inside the package: `Naqel.Files` for files, `Naqel.Streams` for streams. Namespaces separate concepts; packages separate dependencies and configuration.

Remote protocols always get their own package, even when they need no third-party library, as `Naqel.Http` doesn't. Each brings its own client, settings, authentication and error mapping, and most future ones (SFTP, S3, Azure) bring an SDK. Building them only on the public contracts also keeps the extension model honest: if a protocol package can't be written without the core's internals, the contracts are missing something.

How the file, stream and HTTP endpoints behave:

- **`FileTarget`** writes `name.naqel-part`, reserves the full length up front when it's known, writes each part at its offset with positional writes, and renames the file on completion. `FileTarget.At(path)` writes a fixed path; `FileTarget.InDirectory(dir, nameSelector?)` names the file from the source and makes the name safe with the same rules on every OS: only the last path segment is kept, so it can't leave the directory; characters Windows doesn't allow become `_`; trailing dots and spaces go; names Windows reserves (`CON`, `NUL.txt`, `COM1`…) get a `_` in front; and a name longer than 237 UTF-8 bytes is shortened, keeping its extension, so it still fits in 255 bytes with `.naqel-part` and a ` (n)`. `FileConflict` chooses between overwriting an existing file (the default), failing with `AlreadyExists` before transferring, or keeping both (`name (1).ext`). The partial file's path is the resume token.
- **`FileSource`** describes the file with its size and last write time as the version, and fails with `SourceChanged` when either changes.
- **`StreamSource` and `StreamTarget`** wrap a stream the caller owns and never dispose it. A seekable stream takes ranges and parallel parts (each read or write seeks first, one at a time); a stream that can't seek is read once, or written in order, and can't be retried or rewritten.
- **`ConcatenatedSource`** joins ordered pieces, each a source of its own, into one content: the segments of a stream such as HLS, the volumes of a split archive. It describes itself by asking each piece once. When every piece has a length and ranges, it has ranges too, so it splits into parallel parts across piece boundaries and resumes; otherwise it reads the pieces one after another. Its version and identity are fingerprints of the pieces' versions and identities, so it only has a version when every piece has one. A piece that ends before its length fails the read instead of shifting every later byte, and a piece that changed makes the next run describe the pieces again.
- **`HttpSource`** learns the length and range support by asking for byte 0 only (`Range: bytes=0-0`), because many servers answer `HEAD` badly. A strong ETag, or else `Last-Modified`, is the version; resumed ranges send it as `If-Range`. Status codes map to error kinds: 404 and 410 `NotFound`, 401, 403 and 407 `AccessDenied`, 408 `Timeout`, 429 and 5xx transient `Network` with `Retry-After`, and the rest `Protocol`. `HttpTransport.BasicAuthentication(user, password)` is a ready-made request configurator.
- **`HttpTarget`** uploads the whole content in one streamed request: `Put(uri)` or `Post(uri)` send it as the body, `PostForm(uri, field)` as a file field of a `multipart/form-data` form named after `SourceInfo.Name`. The body has a `Content-Length` when the length is known (a form too) and is chunked otherwise; its `Content-Type` is the source's, or `application/octet-stream`. It takes no parallel or partial parts, so a failed upload is sent again from the start, and an accepted one can't be aborted. Rejections map like downloads, plus 413 and 507 to `InsufficientStorage`. `WithResponseHandler` passes the successful answer to the caller, for example to read the id of the uploaded file.

Authentication isn't part of the core: each protocol configures it in its own way, through `HttpClient` handlers or a request configurator for HTTP, credentials on the connection for FTP and SFTP, the SDK's credentials for S3.

### 7.2 Target frameworks

| Build | Used by | Dependencies |
|---|---|---|
| net8.0 | .NET 8 and later | none |
| netstandard2.1 | .NET 5 to 7, .NET Core 3.x, Mono, Unity | `System.Diagnostics.DiagnosticSource` |
| netstandard2.0 | .NET Framework 4.6.2 and later | `System.Diagnostics.DiagnosticSource`, `System.Memory`, `System.Threading.Tasks.Extensions`, `Microsoft.Bcl.AsyncInterfaces`, `Microsoft.Bcl.HashCode` |

- **One public API.** Package validation checks that the builds expose the same types and members, and each project lists its API in `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`: a change to the API fails the build until the list records it. New members go to Unshipped; once a version is published, they move to Shipped.
- **C# 9 or later.** Options, policies and source descriptions are records with `init` setters. .NET Framework projects default to C# 7.3; they need `<LangVersion>9.0</LangVersion>` or later, which works with any .NET Framework.
- **DiagnosticSource 8.0** is the newest version that supports .NET 6 and 7 without warnings, and it has `ActivitySource`, `Meter` and the instruments the engine uses.
- **Fallbacks, not features.** The netstandard builds get the newer APIs the code calls from `src/Compat`: internal polyfills, mostly as C# 14 extension members, so the code reads the same on every target. PolySharp adds the compiler-only types (`IsExternalInit`, the nullable attributes) at build time and isn't a dependency.
- **Where the builds differ:**
  - Without `RandomAccess`, `FileTarget` writes each part through its own unbuffered `FileStream`, which keeps the guarantee that every byte read is on disk before the next read.
  - `HttpTransport.Default` can't use `SocketsHttpHandler` with a pooled connection lifetime on the netstandard builds, so it doesn't notice DNS changes. Pass your own `HttpClient` when that matters.
  - An `HttpRequestException` carries no status code there.
  - .NET Framework allows two connections per server by default, which would make parallel parts beyond two wait until the response timeout fails them. `HttpTransport.Default` sets `MaxConnectionsPerServer` to unlimited, like .NET Core; an `HttpClient` of your own needs at least `MaxConnections`. A real 100 MB download in four throttled parts on .NET Framework 4.8 caught this, which the tests with fake servers couldn't.
- **Tests run against every build:** net10.0 tests the net8.0 build; net48 (on Windows) tests the netstandard2.0 build; and a run with `-p:NaqelTarget=netstandard2.1` makes the net10.0 tests use the netstandard2.1 build, which no current runtime picks by itself. CI runs all three.

## 8. Guarantees the tests pin down

Besides the contracts above, these behaviors each have a test:

1. A resumed range checks the version (the ETag or Last-Modified), and the transfer starts over when the content has changed.
2. Open-ended ranges are resolved to absolute positions from `Content-Range` so they can be resumed.
3. A server that ignores `Range` and answers `200 OK` with the full content is handled: it's accepted for `ByteRange.All` and is an error for any other range.
4. A response that never arrives, and a read that stalls, both time out, even when the source ignores cancellation.
5. Data received before a cancellation reaches the target.
6. A retry that can't resume starts over at offset 0 and overwrites what was written.
7. Exceptions from the caller's code are not retried and keep their type.
8. A checkpoint never counts bytes the target hasn't written, and a stored checkpoint continues the transfer in a later run.
9. Content that fails its checksum never appears under its real name.
10. Content without a version is never spliced from separate reads unless a checksum will catch it.
11. A target that isn't `RepeatableWrites` never gets a part again after it read all of it.
12. A checkpoint only continues the same content: a different identity, version or length starts over, and so does a resume token that doesn't belong to the target.

Property-based tests (with CsCheck) check the engine's guarantees on generated cases rather than examples: transfers with random content, connections, capabilities, retry budgets and faults on any attempt either commit exactly the content or fail only because of an injected fault and commit nothing; a target that isn't `RepeatableWrites` never gets a part twice; checkpoints count only kept bytes and continue to exactly the content; and transfers canceled at random moments end promptly and dispose every stream and session. Bugs planted in the engine make these properties fail, which shows they test something. Damaged checkpoint text, file names from servers and the part planner are fuzzed the same way. `tests/Naqel.Soak` runs the library against real servers on .NET 10 and .NET Framework 4.8, which catches what no fake server shows, such as .NET Framework limiting a client to two connections per server (see [7.2](#72-target-frameworks)).

## 9. Roadmap

After the first release:

- **Protocol packages:** `Naqel.Tus` (resumable HTTP uploads), `Naqel.Sftp`, `Naqel.Ftp`, `Naqel.S3` and `Naqel.AzureBlob`, each built only on the public contracts.
- **A URL registry** that turns a URL into a source or target (`client.CopyAsync("sftp://…", "s3://…")`), where each protocol package registers its schemes.
- **`Naqel.Extensions`** for dependency injection (`services.AddNaqel()`) and `Microsoft.Extensions.Logging`.

## 10. Alternatives considered

- **Separate download and upload types.** Simpler to name, but every engine feature would exist twice, and a copy between two servers would need a third model.
- **Protocols that subclass a base transfer class.** Familiar, but each protocol would inherit and re-implement parts of retrying, throttling and splitting into parts, and the base class would grow with every protocol's needs.
- **Pull-only targets (`OpenWriteAsync` returning a `Stream`).** Simpler for files, but `HttpClient` uploads pull from a stream rather than being written to, so an HTTP target would need a pipe and a background task. Passing a `Stream` to `WritePartAsync` suits both.
- **Use `System.Threading.RateLimiting` and Polly.** Both are good libraries, but they add dependencies to the core for small, self-contained pieces. They can still be plugged in later behind `BandwidthLimiter` and `RetryPolicy`.
- **An `ILogger` in the core.** Familiar to most .NET developers, but it adds a dependency; `ActivitySource` and `Meter` cover tracing and metrics without one (see 6.6).

## 11. Open questions

1. **Unknown-length uploads to part-based targets.** Uploading a non-seekable stream to S3 needs the engine to buffer the stream into parts. Is that worth supporting?
2. **Batches.** Should there be a "many files" API, or is that the job of a separate download manager built on single transfers?
