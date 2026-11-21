namespace Naqel.Manager.Tests.Fakes;

/// <summary>
/// Content in memory that comes slowly, with ranges and a version, so a transfer of it can be paused and continued.
/// It keeps every range it was asked for.
/// </summary>
public sealed class SlowSource(byte[] content, string name, TimeSpan delayPerRead) : ITransferSource
{
    private readonly Lock _lock = new();
    private readonly List<ByteRange> _reads = [];

    public byte[] Content { get; } = content;

    public IReadOnlyList<ByteRange> Reads
    {
        get
        {
            lock (_lock)
            {
                return [.. _reads];
            }
        }
    }

    private SourceInfo Info => new()
    {
        Length = Content.Length,
        SupportsRanges = true,
        Version = "v1",
        Identity = "slow:" + name,
        Name = name
    };

    public Task<SourceInfo> GetInfoAsync(CancellationToken cancellationToken) => Task.FromResult(Info);

    public Task<SourceRead> OpenReadAsync(ReadRequest request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _reads.Add(request.Range);
        }
        var start = (int)request.Range.Start;
        var length = (int)(request.Range.Length ?? Content.Length - start);
        return Task.FromResult(new SourceRead(new SlowStream(new MemoryStream(Content, start, length, writable: false), delayPerRead), Info));
    }

    private sealed class SlowStream(Stream inner, TimeSpan delay) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Resolves <c>slow:name</c> to the slow source of that name, and <c>slow:folder/</c> to all of them with their names as
/// paths, like a folder.
/// </summary>
public sealed class SlowResolver : ISourceResolver
{
    public Dictionary<string, SlowSource> Sources { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether a single file comes with what's known about it; without, like a resolver that saves a request.</summary>
    public bool Describe { get; set; } = true;

    public bool CanResolve(Uri reference) => reference.Scheme == "slow";

    public async IAsyncEnumerable<Resolved> ResolveAsync(Uri reference, ResolveContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var name = reference.AbsolutePath;
        if (name.EndsWith('/'))
        {
            foreach (var (path, source) in Sources.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                yield return new ResolvedSource(source, path, await source.GetInfoAsync(cancellationToken));
            }
            yield break;
        }
        var single = Sources.GetValueOrDefault(name) ?? throw new TransferException($"There's no {name}", TransferErrorKind.NotFound, side: TransferSide.Source);
        yield return new ResolvedSource(single, "", Describe ? await single.GetInfoAsync(cancellationToken) : null);
    }
}
