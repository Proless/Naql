# Changelog

All notable changes to the Naql packages. The packages are released together and share one version.

## 0.1.0-preview.1

The first release: transfers from a source to a target through one shared engine, for files, streams and HTTP. The API may still change before 1.0.

### Naql

- `Transfer.From(source).To(target)`, `TransferClient` and `TransferOperation` to run transfers, with immutable `TransferOptions` and `RetryPolicy`.
- The engine: parallel parts, retries with backoff that continue a part where it stopped, response and idle timeouts (including the wait for an upload's answer), a speed limit per transfer or shared through `BandwidthLimiter`, progress through `IProgress<TransferProgress>`, and one restart when the content changes.
- `TransferResult.Location` says where the content ended up: the file's path, or the uploaded URL.
- `TransferFailedException` for every failed transfer, with the error kind, the side that failed, whether it's transient, the failure itself and every failed part; protocol packages derive their own exceptions from `TransferException`.
- Checkpoints: `GetCheckpoint()`, `ResumeFrom(checkpoint)`, and `WithCheckpoints(ICheckpointStore)` to continue unfinished transfers by themselves, with `FileCheckpointStore`. A checkpoint only continues the same content, by `SourceInfo.Identity` and version, and keeps a fingerprint of the identity rather than the URL.
- Checksum verification with `WithChecksum(Checksum.Sha256("…"))`, hashed while streaming or by reading the target back.
- Traces and metrics through `ActivitySource` and `Meter`, named in `NaqlDiagnostics`.
- `FileSource` and `FileTarget` in `Naql.Files`, and `StreamSource` and `StreamTarget` in `Naql.Streams`. `FileTarget.InDirectory` makes names from sources safe on every OS, including names Windows reserves and names too long for the file system.
- The contracts for new protocols: `ITransferSource`, `ITransferTarget`, `ITargetSession` and `IVerifiableSession`.

### Naql.Http

- `HttpTransferException` with the status code, reason phrase, method and URL of a failed request.
- `HttpSource`: downloads with range requests, `If-Range` when resuming, a one-byte probe to learn the length and range support, the file name from `Content-Disposition`, and the checksum from `Repr-Digest` or `Digest`. It asks for content unencoded and refuses a response the server compressed anyway, whose bytes, length and ranges wouldn't be the content's.
- `HttpTarget`: uploads with `PUT`, `POST` or a `multipart/form-data` form, and `WithResponseHandler` for the server's answer. A `POST` the server received completely is never sent again.
- `HttpTransport` for the `HttpClient`, the User-Agent and request settings such as `BasicAuthentication`.
