namespace Naqel.Plugins;

/// <summary>
/// An account the user signed in to.
/// </summary>
/// <param name="Service">The service, as its provider names it (<see cref="IAccountProvider.Service"/>), such as <c>dropbox</c></param>
/// <param name="Id">The account's stable id at the service</param>
/// <param name="Name">The account's name, to show, such as an email address</param>
public sealed record AccountInfo(string Service, string Id, string Name);
