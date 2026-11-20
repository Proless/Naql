using Naql.Streams;
using Naql.Tests.Fakes;
using System.Security.Cryptography;

namespace Naql.Tests.Streams;

/// <summary>
/// Targets that read the content synchronously, as a library written before async streams might. The engine's
/// streams then take their synchronous paths, which must behave like the asynchronous ones.
/// </summary>
public sealed class SynchronousReadTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        new Random(5).NextBytes(content);
        return content;
    }

    private static TransferOptions Options(int connections) => new()
    {
        MaxConnections = connections,
        MinSplitPartSize = 1_000,
        BufferSize = 1_000,
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false }
    };

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public async Task A_target_that_reads_synchronously_gets_the_whole_content(int connections, bool spans)
    {
        var content = Content(10_000);
        var target = new SynchronousTarget(content.Length, spans);

        await new TransferClient(Options(connections)).From(new MemorySource(content)).To(target).RunAsync(cancellationToken: Token);

        Assert.Equal(content, target.Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_seekable_stream_source_read_synchronously_gives_each_part_its_own_bytes(bool spans)
    {
        var content = Content(10_000);
        var stream = new MemoryStream([1, 2, 3, .. content]) { Position = 3 };
        var target = new SynchronousTarget(content.Length, spans);
        // A checksum lets content without a version be split into parts
        var options = Options(4) with { ExpectedChecksum = new Checksum(HashAlgorithmName.SHA256, SHA256.HashData(content)) };

        var result = await new TransferClient(options).From(StreamSource.From(stream)).To(target).RunAsync(cancellationToken: Token);

        Assert.Equal(content, target.Data);
        Assert.Equal(4, result.Parts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_range_of_a_stream_source_read_synchronously_has_exactly_its_bytes(bool spans)
    {
        var content = Content(10_000);
        var source = StreamSource.From(new MemoryStream(content));
        var target = new SynchronousTarget(content.Length, spans);
        var buffer = new byte[700];

        using var read = await source.OpenReadAsync(new ReadRequest(new ByteRange(2_500, 3_000)), Token);
        var copy = new MemoryStream();
        int n;
        while ((n = target.ReadOnce(read.Content, buffer)) > 0)
        {
            copy.Write(buffer, 0, n);
        }

        Assert.Equal(content.Skip(2_500).Take(3_000).ToArray(), copy.ToArray());
    }

    [Fact]
    public async Task A_synchronous_read_waits_for_the_speed_limit_too()
    {
        var content = Content(20_000);
        var target = new SynchronousTarget(content.Length, spans: false);
        // 20 KB at 50 KB/s takes about 0.4 s
        var options = Options(1) with { MaxBytesPerSecond = 50_000, BufferSize = 10_000 };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await new TransferClient(options).From(new MemorySource(content)).To(target).RunAsync(cancellationToken: Token);

        Assert.Equal(content, target.Data);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(250), $"Took only {clock.Elapsed}");
    }

    /// <summary>
    /// Copies each part with blocking reads, through spans or arrays, into one array.
    /// </summary>
    private sealed class SynchronousTarget(int length, bool spans) : ITransferTarget
    {
        private readonly byte[] _data = new byte[length];

        public byte[] Data => _data;

        public TargetCapabilities Capabilities { get; } = new() { ParallelParts = true, Verifiable = true };

        public Task<ITargetSession> OpenAsync(TargetOpenRequest request, CancellationToken cancellationToken) => Task.FromResult<ITargetSession>(new Session(this));

        private sealed class Session(SynchronousTarget target) : ITargetSession, IVerifiableSession
        {
            public string? Location => null;

            public IReadOnlyDictionary<string, string> Metadata { get; } = new Dictionary<string, string>();

            public string? ResumeToken => null;

            public Task WritePartAsync(ByteRange range, Stream content, CancellationToken cancellationToken)
            {
                return Task.Run(() =>
                {
                    var buffer = new byte[700];
                    var position = (int)range.Start;
                    int read;
                    while ((read = target.ReadOnce(content, buffer)) > 0)
                    {
                        Array.Copy(buffer, 0, target._data, position, read);
                        position += read;
                    }
                }, cancellationToken);
            }

            public Task CompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task AbortAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<byte[]> ComputeHashAsync(HashAlgorithmName algorithm, long length, CancellationToken cancellationToken)
            {
                using var hash = IncrementalHash.CreateHash(algorithm);
                hash.AppendData(target._data, 0, (int)length);
                return Task.FromResult(hash.GetHashAndReset());
            }

            public ValueTask DisposeAsync() => default;
        }

        // On net48 the span overload is the netstandard2.0 build's polyfill
        public int ReadOnce(Stream content, byte[] buffer) => spans ? content.Read(buffer.AsSpan()) : content.Read(buffer, 0, buffer.Length);
    }
}
