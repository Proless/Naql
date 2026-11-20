# Changelog

All notable changes to the Naql packages. The packages are released together and share one version.

## 0.1.0-preview.1

The first release: transfers from a source to a target through one shared engine, for files, streams and HTTP. The API may still change before 1.0.

Both packages build for net8.0 (no dependencies), netstandard2.1 (.NET 5 to 7, .NET Core 3.x, Mono, Unity) and netstandard2.0 (.NET Framework 4.6.2 and later), with the same API. They need C# 9 or later.

### Naql

- `Transfer.From(source).To(target)`, `TransferClient` and `TransferOperation` to run transfers, with immutable `TransferOptions` and `RetryPolicy`.
- The engine: parallel parts, retries with backoff that continue a part where it stopped, response and idle timeouts (including the wait for an upload's answer), a speed limit per transfer or shared through `BandwidthLimiter`, progress through `IProgress<TransferProgress>`, and one restart when the content changes.
- `TransferResult.Location` says where the content ended up: the file's path, or the uploaded URL. `TransferResult.TargetMetadata` says what the target created, such as an upload's ETag.
- Progress through `IProgress<TransferProgress>` ends with a final report however a transfer ends (`Completed`, `Failed` or `Canceled`), and `TransferResult.SingleConnectionReason` says why a transfer used one connection.
- `TransferFailedException` for every failed transfer, with the error kind, the side that failed, whether it's transient, the failure itself and every failed part; protocol packages derive their own exceptions from `TransferException`.
- Checkpoints: `GetCheckpoint()`, `ResumeFrom(checkpoint)`, and `WithCheckpointStore(ICheckpointStore)` to continue unfinished transfers by themselves, with `FileCheckpointStore`. A checkpoint only continues the same content, by `SourceInfo.Identity` and version, and keeps a fingerprint of the identity rather than the URL.
- Checksum verification with `WithExpectedChecksum(Checksum.Sha256("…"))`, hashed while streaming or by reading the target back.
- Traces and metrics through `ActivitySource` and `Meter`, named in `NaqlDiagnostics`.
- `FileSource` and `FileTarget` in `Naql.Files`, and `StreamSource` and `StreamTarget` in `Naql.Streams`. `FileSourceResolver` and `FileTargetResolver` resolve `file:` references, so whole folders copy through resolvers.
- `ConcatenatedSource` joins ordered pieces, such as stream segments or archive volumes, into one content that can still be split into parallel parts and resumed. `FileTarget.InDirectory` makes names from sources safe on every OS, including names Windows reserves and names too long for the file system.
- The contracts for new protocols: `ITransferSource`, `ITransferTarget`, `ITargetSession` and `IVerifiableSession`. `IAcknowledgingSession` is for targets that keep only what their server acknowledged, such as chunked upload sessions: parts continue from the last acknowledged byte, and checkpoints count only those. `TargetCapabilities.PartSizeMultiple` aligns part sizes to what a target requires.
- Resolving references into sources and destinations into targets, for any protocol: `ISourceResolver`, `ITargetResolver`, `ResolvedSource` and `ResolvedReference`, `RelativePath`, and `Resolve` to follow references across resolvers the caller chooses. A `ResolveContext` carries per-call options for every resolver along the way and a filter on the sources found.

### Naql.Http

- `HttpTransferException` with the status code, reason phrase, method and URL of a failed request.
- `HttpSourceOptions` for packages whose services hand out their own links: a stable `Identity`, so a checkpoint fits a new signed link; a `Name`; and `Refresh`, called for a new URL when a link expired (401, 403 or 410), shared by parts that find it expired at the same time.
- `HttpSource`: downloads with range requests, `If-Range` when resuming, a one-byte probe to learn the length and range support, the file name from `Content-Disposition`, and the checksum from `Repr-Digest` or `Digest`. It asks for content unencoded and refuses a response the server compressed anyway, whose bytes, length and ranges wouldn't be the content's.
- `HttpTarget`: uploads with `PUT`, `POST` or a `multipart/form-data` form, and `WithResponseHandler` for the server's answer. A `POST` the server received completely is never sent again.
- `HttpTransport` for the `HttpClient`, the User-Agent and request settings such as `BasicAuthentication`.
