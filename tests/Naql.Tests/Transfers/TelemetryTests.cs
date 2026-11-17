using Naql.Tests.Fakes;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Naql.Tests.Transfers;

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
        MinPartSize = 1_000,
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
            ShouldListenTo = source => source.Name == NaqlDiagnostics.ActivitySourceName,
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
                if (instrument.Meter.Name == NaqlDiagnostics.MeterName)
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
        return new TransferClient(options ?? Options).RunAsync(source, new MemoryTarget(), cancellationToken: Token);
    }

    private static byte[] Content(int length) => new byte[length];

    [Fact]
    public async Task A_transfer_is_traced_with_a_child_per_part()
    {
        await RunAsync(new MemorySource(Content(10_000)), Options with { MaxConnections = 4 });

        var transfer = Assert.Single(Activities("naql.transfer"));
        Assert.Equal(ActivityStatusCode.Ok, transfer.Status);
        Assert.Equal("completed", transfer.GetTagItem("naql.outcome"));
        Assert.Equal(10_000L, transfer.GetTagItem("naql.bytes"));
        Assert.Equal(4, transfer.GetTagItem("naql.parts"));
        Assert.Equal(nameof(MemorySource), transfer.GetTagItem("naql.source.type"));
        Assert.Equal(nameof(MemoryTarget), transfer.GetTagItem("naql.target.type"));

        var parts = Activities("naql.part");
        Assert.Equal(4, parts.Length);
        Assert.All(parts, part => Assert.Equal(transfer.SpanId, part.ParentSpanId));
        Assert.Equal(10_000L, parts.Sum(part => (long)part.GetTagItem("naql.part.bytes")!));
    }

    [Fact]
    public async Task A_retry_is_an_event_on_the_part()
    {
        var source = new MemorySource(Content(10_000))
        {
            WrapContent = (index, _, stream) => index == 0 ? new FailingStream(stream, 2_000, () => new IOException("Reset")) : stream
        };

        await RunAsync(source);

        var retry = Assert.Single(Assert.Single(Activities("naql.part")).Events);
        Assert.Equal("naql.retry", retry.Name);
        Assert.Equal("Network", retry.Tags.Single(tag => tag.Key == "error.type").Value);
        Assert.Equal("Source", retry.Tags.Single(tag => tag.Key == "naql.error.side").Value);

        var counted = Assert.Single(Measurements("naql.transfer.retries"));
        Assert.Equal(1, counted.Value);
        Assert.Equal("Network", counted.Tags["error.type"]);
    }

    [Fact]
    public async Task A_failed_transfer_is_traced_and_counted_with_its_error()
    {
        var source = new MemorySource(Content(100)) { OnOpen = (_, _, _) => throw new TransferException("Gone", TransferErrorKind.NotFound) };

        await Assert.ThrowsAsync<TransferException>(() => RunAsync(source));

        var transfer = Assert.Single(Activities("naql.transfer"));
        Assert.Equal(ActivityStatusCode.Error, transfer.Status);
        Assert.Equal("failed", transfer.GetTagItem("naql.outcome"));
        Assert.Equal("NotFound", transfer.GetTagItem("error.type"));
        Assert.Equal("Source", transfer.GetTagItem("naql.error.side"));

        var ended = Assert.Single(Measurements("naql.transfers"));
        Assert.Equal("failed", ended.Tags["naql.outcome"]);
        Assert.Equal("NotFound", ended.Tags["error.type"]);
    }

    [Fact]
    public async Task Metrics_count_bytes_and_finished_transfers()
    {
        await RunAsync(new MemorySource(Content(10_000)), Options with { MaxConnections = 4 });
        await RunAsync(new MemorySource(Content(5_000)));

        Assert.Equal(15_000, Measurements("naql.transfer.bytes").Sum(m => m.Value));
        Assert.Equal(2, Measurements("naql.transfers").Count(m => (string?)m.Tags["naql.outcome"] == "completed"));
        Assert.Equal(2, Measurements("naql.transfer.duration").Length);
        // Every transfer that started also ended
        Assert.Equal(0, Measurements("naql.transfers.active").Sum(m => m.Value));
    }

    [Fact]
    public async Task A_canceled_transfer_is_counted_as_canceled()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var source = new MemorySource(Content(100)) { OnOpen = (_, _, ct) => Task.Delay(Timeout.Infinite, ct) };
        cts.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new TransferClient(Options).RunAsync(source, new MemoryTarget(), cancellationToken: cts.Token));

        Assert.Equal("canceled", Assert.Single(Activities("naql.transfer")).GetTagItem("naql.outcome"));
        Assert.Equal("canceled", Assert.Single(Measurements("naql.transfers")).Tags["naql.outcome"]);
    }
}
