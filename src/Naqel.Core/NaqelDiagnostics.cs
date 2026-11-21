namespace Naqel;

/// <summary>
/// The names under which Naqel reports traces and metrics through <see cref="System.Diagnostics.ActivitySource"/> and
/// <see cref="System.Diagnostics.Metrics.Meter"/>, for example to subscribe with OpenTelemetry
/// (<c>AddSource(NaqelDiagnostics.ActivitySourceName)</c>, <c>AddMeter(NaqelDiagnostics.MeterName)</c>) or to watch them
/// with <c>dotnet-counters</c>.
/// </summary>
/// <remarks>
/// <para>Traces: a <c>naqel.transfer</c> activity per transfer, with a <c>naqel.part</c> child per part. A retry adds a
/// <c>naqel.retry</c> event to the part (or to the transfer for describing the content, opening and completing the
/// target), and starting over because the content changed adds a <c>naqel.restart</c> event.</para>
/// <para>Metrics: <c>naqel.transfer.bytes</c>, <c>naqel.transfer.retries</c>, <c>naqel.transfers</c> (finished transfers
/// by <c>naqel.outcome</c>), <c>naqel.transfer.duration</c> and <c>naqel.transfers.active</c>.</para>
/// </remarks>
public static class NaqelDiagnostics
{
    /// <summary>
    /// The name of the activity source transfers are traced with.
    /// </summary>
    public const string ActivitySourceName = "Naqel";

    /// <summary>
    /// The name of the meter transfers are measured with.
    /// </summary>
    public const string MeterName = "Naqel";
}
