namespace Naql.Tests.Fakes;

/// <summary>
/// A target that keeps the content in memory and records what the engine did with it.
/// </summary>
public sealed class MemoryTarget : ITransferTarget
{
    private readonly object _lock = new();
    private readonly List<MemorySession> _sessions = [];

    public TargetCapabilities Capabilities { get; init; } = new() { ParallelParts = true, ResumableParts = true, Resumable = true };

    /// <summary>
    /// Runs before a part is read, with the number of the write; throw to fail it, or wait to stall it.
    /// </summary>
    public Func<int, ByteRange, CancellationToken, Task>? OnWrite { get; init; }

    /// <summary>
    /// Runs after a write read its whole part, with the number of the write; throw to fail it, or wait to stall it, like
    /// a server that has the whole upload but hasn't answered.
    /// </summary>
    public Func<int, CancellationToken, Task>? AfterRead { get; init; }

    /// <summary>
    /// Runs before a session opens, with the number of the open; throw to fail it, or wait to stall it.
    /// </summary>
    public Func<int, CancellationToken, Task>? OnOpen { get; init; }

    /// <summary>
    /// Runs when a session completes; throw to fail it, or wait to stall it.
    /// </summary>
    public Func<CancellationToken, Task>? OnComplete { get; init; }

    /// <summary>
    /// Runs when a session aborts; throw to fail it, or wait to stall it.
    /// </summary>
    public Func<CancellationToken, Task>? OnAbort { get; init; }

    private int _opens;

    /// <summary>
    /// The first write fails with an <see cref="IOException"/> after keeping this many bytes, like a full disk.
    /// </summary>
    public long? FailFirstWriteAfter { get; init; }

    /// <summary>
    /// Every write stops reading after this many bytes and returns as if it were done.
    /// </summary>
    public long? StopReadingAfter { get; init; }

    public IReadOnlyList<MemorySession> Sessions
    {
        get
        {
            lock (_lock)
            {
                return _sessions.ToArray();
            }
        }
    }

    /// <summary>
    /// The only session.
    /// </summary>
    public MemorySession Session => Assert.Single(Sessions);

    /// <summary>
    /// Opens a session; with a resume token, the new session starts with the data of the session that token names.
    /// </summary>
    public async Task<ITargetSession> OpenAsync(TargetOpenRequest request, CancellationToken cancellationToken)
    {
        var open = Interlocked.Increment(ref _opens) - 1;
        if (OnOpen != null)
        {
            await OnOpen(open, cancellationToken);
        }

        lock (_lock)
        {
            var session = new MemorySession(this, request) { ResumeToken = $"memory-{_sessions.Count}" };
            if (request.ResumeToken != null)
            {
                var earlier = _sessions.FirstOrDefault(s => s.ResumeToken == request.ResumeToken && !s.Aborted)
                    ?? throw new TransferException($"No partial data for '{request.ResumeToken}'", TransferErrorKind.NotFound);
                session.CopyFrom(earlier);
            }
            _sessions.Add(session);
            return session;
        }
    }
}

public sealed class MemorySession(MemoryTarget target, TargetOpenRequest request) : ITargetSession, IVerifiableSession
{
    private readonly object _lock = new();
    private readonly List<ByteRange> _writes = [];
    private byte[] _data = new byte[1024];
    private long _length;
    private int _activeWrites;
    private int _maxActiveWrites;

    private int _completedWrites;

    public TargetOpenRequest Request { get; } = request;
    public string? ResumeToken { get; init; }

    /// <summary>
    /// The writes that read their part to the end.
    /// </summary>
    public int CompletedWrites => Volatile.Read(ref _completedWrites);

    internal void CopyFrom(MemorySession earlier)
    {
        lock (_lock)
        {
            _data = earlier.Data;
            _length = _data.Length;
            Array.Resize(ref _data, Math.Max(_data.Length, 1024));
        }
    }
    public bool Completed { get; private set; }
    public bool Aborted { get; private set; }
    public bool Disposed { get; private set; }

    public IReadOnlyList<ByteRange> Writes
    {
        get
        {
            lock (_lock)
            {
                return _writes.ToArray();
            }
        }
    }

    public int MaxActiveWrites => Volatile.Read(ref _maxActiveWrites);

    public byte[] Data
    {
        get
        {
            lock (_lock)
            {
                return _data.AsSpan(0, (int)_length).ToArray();
            }
        }
    }

    public async Task WritePartAsync(ByteRange range, Stream content, CancellationToken cancellationToken)
    {
        int index;
        lock (_lock)
        {
            index = _writes.Count;
            _writes.Add(range);
            _maxActiveWrites = Math.Max(_maxActiveWrites, ++_activeWrites);
        }

        try
        {
            if (target.OnWrite != null)
            {
                await target.OnWrite(index, range, cancellationToken);
            }

            var buffer = new byte[4096];
            var position = range.Start;
            long written = 0;
            while (true)
            {
                var toRead = buffer.Length;
                if (target.StopReadingAfter is long stop)
                {
                    if (written >= stop)
                    {
                        return;
                    }
                    toRead = (int)Math.Min(toRead, stop - written);
                }

                var read = await content.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                if (read == 0)
                {
                    Interlocked.Increment(ref _completedWrites);
                    if (target.AfterRead != null)
                    {
                        await target.AfterRead(index, cancellationToken);
                    }
                    return;
                }

                // Every byte read is kept at once, which is what ResumableParts promises
                Store(position, buffer, read);
                position += read;
                written += read;

                if (index == 0 && written >= target.FailFirstWriteAfter)
                {
                    throw new IOException("The disk is full");
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _activeWrites--;
            }
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (target.OnComplete != null)
        {
            await target.OnComplete(cancellationToken);
        }
        Completed = true;
    }

    public async Task AbortAsync(CancellationToken cancellationToken)
    {
        Aborted = true;
        if (target.OnAbort != null)
        {
            await target.OnAbort(cancellationToken);
        }
    }

    /// <summary>
    /// How often the engine read the data back to verify a checksum.
    /// </summary>
    public int HashReads { get; private set; }

    public Task<byte[]> ComputeHashAsync(System.Security.Cryptography.HashAlgorithmName algorithm, long length, CancellationToken cancellationToken)
    {
        HashReads++;
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(algorithm);
        hash.AppendData(Data.AsSpan(0, (int)length));
        return Task.FromResult(hash.GetHashAndReset());
    }

    public void Dispose() => Disposed = true;

    private void Store(long position, byte[] buffer, int count)
    {
        lock (_lock)
        {
            var end = position + count;
            if (end > _data.Length)
            {
                Array.Resize(ref _data, (int)Math.Max(end, _data.Length * 2L));
            }
            Array.Copy(buffer, 0, _data, position, count);
            _length = Math.Max(_length, end);
        }
    }
}
