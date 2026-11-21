namespace Naqel.Plugins;

/// <summary>
/// Why a job failed, in the same terms as a failed transfer, whatever engine ran it.
/// </summary>
/// <param name="Kind">What kind of error it is</param>
/// <param name="Message">What went wrong, for the user</param>
/// <param name="IsTransient">Whether trying again later can succeed</param>
public sealed record JobError(TransferErrorKind Kind, string Message, bool IsTransient)
{
    /// <summary>
    /// The error for <paramref name="exception"/>: a <see cref="TransferException"/> keeps its kind and whether it's
    /// transient; anything else is a non-transient <see cref="TransferErrorKind.Unknown"/>.
    /// </summary>
    public static JobError From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is TransferException transfer
            ? new JobError(transfer.Kind, transfer.Message, transfer.IsTransient)
            : new JobError(TransferErrorKind.Unknown, exception.Message, IsTransient: false);
    }
}
