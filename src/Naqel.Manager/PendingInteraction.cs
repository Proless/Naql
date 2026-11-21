using Naqel.Plugins;

namespace Naqel.Manager;

/// <summary>
/// A question waiting for the user's answer.
/// </summary>
/// <param name="Id">Its id, to answer it with <see cref="Interactions.Answer"/></param>
/// <param name="Request">What is asked</param>
/// <param name="Asked">When it was asked</param>
public sealed record PendingInteraction(string Id, InteractionRequest Request, DateTimeOffset Asked);
