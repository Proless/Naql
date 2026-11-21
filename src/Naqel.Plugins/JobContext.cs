namespace Naqel.Plugins;

/// <summary>
/// The job a resolution is for, which Naqel's engine puts into the <see cref="ResolveContext"/> it resolves with, so a
/// resolver can ask the user about that job: <c>context.Get&lt;JobContext&gt;()</c>.
/// </summary>
/// <param name="JobId">The manager's id for the job</param>
/// <param name="Interaction">Asks the user; the job needs attention until the user answers</param>
public sealed record JobContext(string JobId, IInteraction Interaction);
