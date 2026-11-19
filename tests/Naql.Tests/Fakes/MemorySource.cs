namespace Naql.Tests.Fakes;

/// <summary>
/// A source that serves bytes from memory, with hooks to make reads fail, stall or change version.
/// </summary>
public sealed class MemorySource(byte[] content) : ITransferSource
{
    private readonly object _lock = new();
    private readonly List<ReadRequest> _reads = [];
    private int _infoCalls;

    public byte[] Content { get; set; } = content;
    public string? Version { get; set; } = "v1";
    public string? Identity { get; init; } = "memory://content";
    public bool SupportsRanges { get; init; } = true;
    public bool IsRepeatable { get; init; } = true;
    public bool ReportsLength { get; init; } = true;

    /// <summary>
    /// The checksum the source reports for its content.
    /// </summary>
    public Checksum? Checksum { get; init; }

    /// <summary>
    /// Runs before <see cref="GetInfoAsync"/> answers, with the number of the call; throw to fail it.
    /// </summary>
    public Func<int, CancellationToken, Task>? OnGetInfo { get; init; }

    /// <summary>
    /// Runs before a read is opened, with the number of the read; throw to fail it, or wait to delay it.
    /// </summary>
    public Func<int, ReadRequest, CancellationToken, Task>? OnOpen { get; init; }

    /// <summary>
    /// Replaces the content stream of a read, to make it fail, end early or stall.
    /// </summary>
    public Func<int, ReadRequest, Stream, Stream>? WrapContent { get; init; }

    public int InfoCalls => Volatile.Read(ref _infoCalls);

    public IReadOnlyList<ReadRequest> Reads
    {
        get
        {
            lock (_lock)
            {
                return _reads.ToArray();
            }
        }
    }

    public SourceInfo Info => new()
    {
        Length = ReportsLength ? Content.Length : null,
        SupportsRanges = SupportsRanges,
        IsRepeatable = IsRepeatable,
        Identity = Identity,
        Version = Version,
        Name = "memory.bin",
        Checksum = Checksum
    };

    public async Task<SourceInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _infoCalls) - 1;
        if (OnGetInfo != null)
        {
            await OnGetInfo(call, cancellationToken);
        }
        return Info;
    }

    public async Task<SourceRead> OpenReadAsync(ReadRequest request, CancellationToken cancellationToken)
    {
        int index;
        lock (_lock)
        {
            index = _reads.Count;
            _reads.Add(request);
        }

        if (OnOpen != null)
        {
            await OnOpen(index, request, cancellationToken);
        }
        if (request.IfVersion != null && request.IfVersion != Version)
        {
            throw new TransferException("The content changed", TransferErrorKind.SourceChanged);
        }
        if (!SupportsRanges && !request.Range.IsAll)
        {
            throw new InvalidOperationException($"Asked for {request.Range} from a source without ranges");
        }

        // Describe the content as it was when the read opened, like a response header would
        var info = Info;
        var start = (int)request.Range.Start;
        var length = (int)(request.Range.Length ?? Content.Length - start);
        Stream stream = new MemoryStream(Content, start, length, writable: false);
        if (WrapContent != null)
        {
            stream = WrapContent(index, request, stream);
        }
        return new SourceRead(stream, info);
    }
}
