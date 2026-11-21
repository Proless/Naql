using Naqel.Core.Tests.Fakes;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Naqel.Core.Tests.Transfers;

/// <summary>
/// Listeners see every transfer in the process, so these tests never run alongside others.
/// </summary>
[CollectionDefinition(nameof(TelemetryCollection), DisableParallelization = true)]
public sealed class TelemetryCollection;

/// <summary>
/// The traces and metrics transfers report.
/// </summary>
[Collection(nameof(TelemetryCollection))]
public sealed class TelemetryTests : IDisposable
{
    private static readonly TransferOptions Options = new()
    {
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false },
        MinSplitPartSize = 1_000,
        BufferSize = 1_000
    };

    private readonly List<Activity> _activities = [];
    private readonly List<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = [];
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public TelemetryTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NaqelDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_activities)
                {
                    _activities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == NaqelDiagnostics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meterListener.Start();
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }
        lock (_measurements)
        {
            _measurements.Add((instrument.Name, value, copy));
        }
    }

    private Activity[] Activities(string name)
    {
        lock (_activities)
        {
            return _activities.Where(activity => activity.OperationName == name).ToArray();
        }
    }

    private (double Value, Dictionary<string, object?> Tags)[] Measurements(string instrument)
    {
        lock (_measurements)
        {
            return _measurements.Where(m => m.Instrument == instrument).Select(m => (m.Value, m.Tags)).ToArray();
        }
    }

    private static Task<TransferResult> RunAsync(ITransferSource source, TransferOptions? options = null)
    {
        return new TransferClient(options ?? Options).From(source).To(new MemoryTarget()).RunAsync(cancellationToken: Token);
    }

    private static byte[] Content(int length) => new byte[length];

    [Fact]
    public async Task A_transfer_is_traced_with_a_child_per_part()
    {
        await RunAsync(new MemorySource(Content(10_000)), Options with { MaxConnections = 4 });

        var transfer = Assert.Single(Activities("naqel.transfer"));
        Assert.Equal(ActivityStatusCode.Ok, transfer.Status);
        Assert.Equal("completed", transfer.GetTagItem("naqel.outcome"));
        Assert.Equal(10_000L, transfer.GetTagItem("naqel.bytes"));
        Assert.Equal(4, transfer.GetTagItem("naqel.parts"));
        Assert.Equal(nameof(MemorySource), transfer.GetTagItem("naqel.source.type"));
        Assert.Equal(nameof(MemoryTarget), transfer.GetTagItem("naqel.target.type"));

        var parts = Activities("naqel.part");
        Assert.Equal(4, parts.Length);
        Assert.All(parts, part => Assert.Equal(transfer.SpanId, part.ParentSpanId));
        Assert.Equal(10_000L, parts.Sum(part => (long)part.GetTagItem("naqel.part.bytes")!));
    }

    [Fact]
    public async Task A_retry_is_an_event_on_the_part()
    {
        var source = new MemorySource(Content(10_000))
        {
            WrapContent = (index, _, stream) => index == 0 ? new FailingStream(stream, 2_000, () => new IOException("Reset")) : stream
        };

        await RunAsync(source);

        var retry = Assert.Single(Assert.Single(Activities("naqel.part")).Events);
        Assert.Equal("naqel.retry", retry.Name);
        Assert.Equal("Network", retry.Tags.Single(tag => tag.Key == "error.type").Value);
        Assert.Equal("Source", retry.Tags.Single(tag => tag.Key == "naqel.error.side").Value);

        var counted = Assert.Single(Measurements("naqel.transfer.retries"));
        Assert.Equal(1, counted.Value);
        Assert.Equal("Network", counted.Tags["error.type"]);
    }

    [Fact]
    public async Task A_failed_transfer_is_traced_and_counted_with_its_error()
    {
        var source = new MemorySource(Content(100)) { OnOpen = (_, _, _) => throw new TransferException("Gone", TransferErrorKind.NotFound) };

        await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source));

        var transfer = Assert.Single(Activities("naqel.transfer"));
        Assert.Equal(ActivityStatusCode.Error, transfer.Status);
        Assert.Equal("failed", transfer.GetTagItem("naqel.outcome"));
        Assert.Equal("NotFound", transfer.GetTagItem("error.type"));
        Assert.Equal("Source", transfer.GetTagItem("naqel.error.side"));

        var ended = Assert.Single(Measurements("naqel.transfers"));
        Assert.Equal("failed", ended.Tags["naqel.outcome"]);
        Assert.Equal("NotFound", ended.Tags["error.type"]);
    }

    [Fact]
    public async Task Metrics_count_bytes_and_finished_transfers()
    {
        await RunAsync(new MemorySource(Content(10_000)), Options with { MaxConnections = 4 });
        await RunAsync(new MemorySource(Content(5_000)));

        Assert.Equal(15_000, Measurements("naqel.transfer.bytes").Sum(m => m.Value));
        Assert.Equal(2, Measurements("naqel.transfers").Count(m => (string?)m.Tags["naqel.outcome"] == "completed"));
        Assert.Equal(2, Measurements("naqel.transfer.duration").Length);
        // Every transfer that started also ended
        Assert.Equal(0, Measurements("naqel.transfers.active").Sum(m => m.Value));
    }

    [Fact]
    public async Task A_canceled_transfer_is_counted_as_canceled()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var source = new MemorySource(Content(100)) { OnOpen = (_, _, ct) => Task.Delay(Timeout.Infinite, ct) };
        cts.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new TransferClient(Options).From(source).To(new MemoryTarget()).RunAsync(cancellationToken: cts.Token));

        Assert.Equal("canceled", Assert.Single(Activities("naqel.transfer")).GetTagItem("naqel.outcome"));
        Assert.Equal("canceled", Assert.Single(Measurements("naqel.transfers")).Tags["naqel.outcome"]);
    }
}
