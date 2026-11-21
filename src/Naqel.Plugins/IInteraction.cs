namespace Naqel.Plugins;

/// <summary>
/// Asks the user something, instead of failing, such as a password for a protected link or which quality to download.
/// The desktop app asks right away; the server keeps the request until the user answers in a UI or through its API.
/// </summary>
/// <remarks>
/// A request may wait for a long time, and the answer may never come before the job is removed or Naqel stops, which
/// cancels the wait. Plugins never solve captchas themselves; they ask the user.
/// </remarks>
public interface IInteraction
{
    /// <summary>
    /// Asks the user, and waits for the answer.
    /// </summary>
    /// <returns>The answer, which may be that the user declined</returns>
    Task<InteractionAnswer> AskAsync(InteractionRequest request, CancellationToken cancellationToken);
}
