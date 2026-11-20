namespace Naql.Tests.Fakes;

/// <summary>
/// An upload service like Drive's or OneDrive's resumable uploads: content goes up in chunks, in order, and the server
/// keeps a chunk once it acknowledged it. Its target's sessions are <see cref="IAcknowledgingSession"/>s.
/// </summary>
public sealed class ChunkedUploadServer
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Upload> _uploads = [];
    private int _sends;

    /// <summary>
    /// Runs before the server takes a chunk, with the number of the send; throw to make it fail, like a dropped
    /// connection: the session read the chunk, but the server never acknowledged it.
    /// </summary>
    public Func<int, Task>? OnSend { get; init; }

    public int Sends => Volatile.Read(ref _sends);

    public sealed class Upload
    {
        public byte[] Data { get; set; } = [];
        public long Acknowledged { get; set; }
        public bool Completed { get; set; }
    }

    public Upload Get(string id)
    {
        lock (_lock)
        {
            return _uploads[id];
        }
    }

    /// <summary>
    /// Makes the server forget everything after <paramref name="acknowledged"/> bytes of an upload, as if it lost them.
    /// </summary>
    public void Forget(string id, long acknowledged)
    {
        lock (_lock)
        {
            _uploads[id].Acknowledged = acknowledged;
        }
    }

    public ChunkedUploadTarget Target(int chunkSize, bool verifiable = true) => new(this, chunkSize, verifiable);

    internal Upload Start(string id, long? length)
    {
        lock (_lock)
        {
            var upload = new Upload { Data = new byte[length ?? 0] };
            _uploads[id] = upload;
            return upload;
        }
    }

    internal Upload? Find(string id)
    {
        lock (_lock)
        {
            return _uploads.TryGetValue(id, out var upload) ? upload : null;
        }
    }

    internal async Task SendAsync(Upload upload, long offset, byte[] chunk, int count)
    {
        var send = Interlocked.Increment(ref _sends) - 1;
        if (OnSend != null)
        {
            await OnSend(send);
        }
        lock (_lock)
        {
            if (offset != upload.Acknowledged)
            {
                throw new TransferException($"The server expected the chunk at {upload.Acknowledged}, not {offset}", TransferErrorKind.Protocol, side: TransferSide.Target);
            }
            if (upload.Data.Length < offset + count)
            {
                var data = upload.Data;
                Array.Resize(ref data, (int)(offset + count));
                upload.Data = data;
            }
            Array.Copy(chunk, 0, upload.Data, offset, count);
            upload.Acknowledged = offset + count;
        }
    }
}

public sealed class ChunkedUploadTarget(ChunkedUploadServer server, int chunkSize, bool verifiable) : ITransferTarget
{
    private int _uploads;

    public List<ChunkedUploadSession> Sessions { get; } = [];

    public TargetCapabilities Capabilities { get; } = new() { ResumableParts = true, Resumable = true, Verifiable = verifiable };

    public Task<ITargetSession> OpenAsync(TargetOpenRequest request, CancellationToken cancellationToken)
    {
        string id;
        ChunkedUploadServer.Upload upload;
        if (request.ResumeToken is string token)
        {
            // Reattach to the upload, and ask the server where it stands
            id = token;
            upload = server.Find(token) ?? throw new TransferException($"No upload {token}", TransferErrorKind.NotFound, side: TransferSide.Target);
        }
        else
        {
            id = $"upload-{Interlocked.Increment(ref _uploads)}";
            upload = server.Start(id, request.Source.Length);
        }

        var session = new ChunkedUploadSession(server, upload, id, chunkSize);
        lock (Sessions)
        {
            Sessions.Add(session);
        }
        return Task.FromResult<ITargetSession>(session);
    }
}

public sealed class ChunkedUploadSession(ChunkedUploadServer server, ChunkedUploadServer.Upload upload, string id, int chunkSize)
    : ITargetSession, IAcknowledgingSession, IVerifiableSession
{
    // What this session knows the server acknowledged; asked from the server when the session opened
    private long _acknowledged = upload.Acknowledged;

    public string? ResumeToken => id;
    public string? Location => $"chunked://{id}";
    public IReadOnlyDictionary<string, string> Metadata { get; } = new Dictionary<string, string> { ["upload"] = id };
    public bool Disposed { get; private set; }

    public long Kept(long position) => Math.Max(0, Interlocked.Read(ref _acknowledged) - position);

    public async Task WritePartAsync(ByteRange range, Stream content, CancellationToken cancellationToken)
    {
        var offset = range.Start;
        var chunk = new byte[chunkSize];
        while (true)
        {
            // Fill a whole chunk before sending it, as upload sessions do
            var filled = 0;
            int read;
            while (filled < chunkSize && (read = await content.ReadAsync(chunk.AsMemory(filled, chunkSize - filled), cancellationToken)) > 0)
            {
                filled += read;
            }
            if (filled == 0)
            {
                return;
            }

            await server.SendAsync(upload, offset, chunk, filled);
            offset += filled;
            Interlocked.Exchange(ref _acknowledged, offset);
        }
    }

    public Task CompleteAsync(CancellationToken cancellationToken)
    {
        upload.Completed = true;
        return Task.CompletedTask;
    }

    public Task AbortAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<byte[]> ComputeHashAsync(System.Security.Cryptography.HashAlgorithmName algorithm, long length, CancellationToken cancellationToken)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(algorithm);
        hash.AppendData(upload.Data, 0, (int)length);
        return Task.FromResult(hash.GetHashAndReset());
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return default;
    }
}
