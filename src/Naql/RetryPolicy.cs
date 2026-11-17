using System;

namespace Naql;

/// <summary>
/// How often and how long to wait before retrying a transient error.
/// </summary>
/// <remarks>
/// The delay before retry <c>n</c> is <see cref="InitialDelay"/> × <see cref="BackoffFactor"/>^(n-1), capped at
/// <see cref="MaxDelay"/>, varied by ±20% when <see cref="Jitter"/> is on, and never shorter than a
/// <see cref="TransferException.RetryAfter"/> the other side asked for.
/// </remarks>
public sealed record RetryPolicy
{
    /// <summary>
    /// 3 retries, waiting 1 second, then twice as long each time up to 30 seconds, with jitter.
    /// </summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>
    /// Never retry.
    /// </summary>
    public static RetryPolicy None { get; } = new() { MaxRetries = 0 };

    /// <summary>
    /// How many times to try again after the first attempt fails.
    /// </summary>
    public int MaxRetries
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(MaxRetries));
            field = value;
        }
    } = 3;

    /// <summary>
    /// The delay before the first retry.
    /// </summary>
    public TimeSpan InitialDelay
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(InitialDelay));
            field = value;
        }
    } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest delay between two attempts, unless the other side asks for a longer one.
    /// </summary>
    public TimeSpan MaxDelay
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(MaxDelay));
            field = value;
        }
    } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How much longer each delay is than the one before. 1 keeps the delay constant.
    /// </summary>
    public double BackoffFactor
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(BackoffFactor));
            field = value;
        }
    } = 2;

    /// <summary>
    /// Varies each delay randomly by up to 20%, so parts and clients that failed together don't all retry at once.
    /// </summary>
    public bool Jitter { get; init; } = true;

    /// <summary>
    /// The delay before a retry.
    /// </summary>
    /// <param name="retry">Which retry this is, starting at 1</param>
    /// <param name="retryAfter">How long the other side asked to wait, if it did</param>
    public TimeSpan GetDelay(int retry, TimeSpan? retryAfter = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retry, 1);

        var milliseconds = Math.Min(InitialDelay.TotalMilliseconds * Math.Pow(BackoffFactor, retry - 1), MaxDelay.TotalMilliseconds);
        if (Jitter && milliseconds > 0)
        {
            milliseconds = Math.Min(milliseconds * (0.8 + (0.4 * Random.Shared.NextDouble())), MaxDelay.TotalMilliseconds);
        }

        var delay = TimeSpan.FromMilliseconds(milliseconds);
        return retryAfter > delay ? retryAfter.Value : delay;
    }
}
