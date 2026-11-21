using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Naqel.Engine;

/// <summary>
/// The activity source and instruments transfers report to. Without a listener they cost next to nothing.
/// </summary>
internal static class Telemetry
{
    private static readonly string? Version = typeof(Telemetry).Assembly.GetName().Version?.ToString(3);

    public static readonly ActivitySource Source = new(NaqelDiagnostics.ActivitySourceName, Version);

    private static readonly Meter Meter = new(NaqelDiagnostics.MeterName, Version);

    public static readonly Counter<long> Bytes = Meter.CreateCounter<long>(
        "naqel.transfer.bytes", "By", "Bytes moved from sources to targets");

    public static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        "naqel.transfer.retries", "{retry}", "Operations retried after a transient error");

    public static readonly Counter<long> Transfers = Meter.CreateCounter<long>(
        "naqel.transfers", "{transfer}", "Transfers that ended, by outcome");

    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "naqel.transfer.duration", "s", "How long transfers ran, by outcome");

    public static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>(
        "naqel.transfers.active", "{transfer}", "Transfers running right now");

    // Tag names, following OpenTelemetry's conventions where one exists
    public const string SourceType = "naqel.source.type";
    public const string TargetType = "naqel.target.type";
    public const string Outcome = "naqel.outcome";
    public const string ErrorType = "error.type";
    public const string ErrorSide = "naqel.error.side";
    public const string ErrorTransient = "naqel.error.transient";
}
