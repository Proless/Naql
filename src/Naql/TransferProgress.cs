using System;

namespace Naql;

/// <summary>
/// What a transfer is doing.
/// </summary>
public enum TransferState
{
    /// <summary>Describing the content, planning parts and opening the target.</summary>
    Preparing,

    /// <summary>Moving bytes.</summary>
    Transferring,

    /// <summary>At least one part is waiting to try again after an error.</summary>
    Retrying,

    /// <summary>All bytes have moved and the target is making the result visible.</summary>
    Completing,

    /// <summary>The transfer finished successfully.</summary>
    Completed
}

/// <summary>
/// A snapshot of a running transfer, reported through <see cref="IProgress{T}"/>.
/// </summary>
public sealed class TransferProgress
{
    internal TransferProgress(TransferState state, long transferred, long? total, double currentRate, double averageRate,
        TimeSpan elapsed, int activeParts, int completedParts, int totalParts, Exception? lastError)
    {
        State = state;
        Transferred = transferred;
        Total = total;
        CurrentRate = currentRate;
        AverageRate = averageRate;
        Elapsed = elapsed;
        ActiveParts = activeParts;
        CompletedParts = completedParts;
        TotalParts = totalParts;
        LastError = lastError;
    }

    /// <summary>
    /// What the transfer is doing.
    /// </summary>
    public TransferState State { get; }

    /// <summary>
    /// How many bytes have moved. It can go down when a part has to start over.
    /// </summary>
    public long Transferred { get; }

    /// <summary>
    /// The length of the content, or <c>null</c> while it's unknown.
    /// </summary>
    public long? Total { get; }

    /// <summary>
    /// How much is done, from 0 to 1, or <c>null</c> while the length is unknown.
    /// </summary>
    public double? Fraction => Total is > 0 ? Math.Min(1, (double)Transferred / Total.Value) : Total == 0 ? 1 : null;

    /// <summary>
    /// Bytes per second over the last few seconds.
    /// </summary>
    public double CurrentRate { get; }

    /// <summary>
    /// Bytes per second since the transfer started.
    /// </summary>
    public double AverageRate { get; }

    /// <summary>
    /// The time since the transfer started.
    /// </summary>
    public TimeSpan Elapsed { get; }

    /// <summary>
    /// An estimate of the time left at <see cref="CurrentRate"/>, or <c>null</c> when it can't be estimated.
    /// </summary>
    public TimeSpan? Remaining => Total is long total && CurrentRate > 0
        ? TimeSpan.FromSeconds(Math.Max(0, total - Transferred) / CurrentRate)
        : null;

    /// <summary>
    /// The parts that are moving bytes right now.
    /// </summary>
    public int ActiveParts { get; }

    /// <summary>
    /// The parts that are done.
    /// </summary>
    public int CompletedParts { get; }

    /// <summary>
    /// All parts of the transfer, or 0 before they are planned.
    /// </summary>
    public int TotalParts { get; }

    /// <summary>
    /// The most recent error a part is retrying after, or <c>null</c>.
    /// </summary>
    public Exception? LastError { get; }
}
