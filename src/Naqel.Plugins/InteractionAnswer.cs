namespace Naqel.Plugins;

/// <summary>
/// The user's answer to an <see cref="InteractionRequest"/>.
/// </summary>
/// <param name="Value">The answer: the text, the choice, <c>yes</c> or <c>no</c>, or what a page handed back; <c>null</c>
/// when the user declined</param>
public sealed record InteractionAnswer(string? Value)
{
    /// <summary>
    /// The user declined to answer.
    /// </summary>
    public static InteractionAnswer Declined { get; } = new((string?)null);

    /// <summary>
    /// Whether the user declined to answer.
    /// </summary>
    public bool IsDeclined => Value == null;
}
