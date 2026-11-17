namespace Naql;

/// <summary>
/// The names under which Naql reports traces and metrics through <see cref="System.Diagnostics.ActivitySource"/> and
/// <see cref="System.Diagnostics.Metrics.Meter"/>, for example to subscribe with OpenTelemetry
/// (<c>AddSource(NaqlDiagnostics.ActivitySourceName)</c>, <c>AddMeter(NaqlDiagnostics.MeterName)</c>) or to watch them
/// with <c>dotnet-counters</c>.
/// </summary>
/// <remarks>
/// <para>Traces: a <c>naql.transfer</c> activity per transfer, with a <c>naql.part</c> child per part. A retry adds a
/// <c>naql.retry</c> event to the part (or to the transfer for describing the content, opening and completing the
/// target), and starting over because the content changed adds a <c>naql.restart</c> event.</para>
/// <para>Metrics: <c>naql.transfer.bytes</c>, <c>naql.transfer.retries</c>, <c>naql.transfers</c> (finished transfers
/// by <c>naql.outcome</c>), <c>naql.transfer.duration</c> and <c>naql.transfers.active</c>.</para>
/// </remarks>
public static class NaqlDiagnostics
{
    /// <summary>
    /// The name of the activity source transfers are traced with.
    /// </summary>
    public const string ActivitySourceName = "Naql";

    /// <summary>
    /// The name of the meter transfers are measured with.
    /// </summary>
    public const string MeterName = "Naql";
}
