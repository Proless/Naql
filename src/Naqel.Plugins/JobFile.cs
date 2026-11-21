namespace Naqel.Plugins;

/// <summary>
/// One file of a job, such as one file of a folder or of a torrent.
/// </summary>
/// <param name="Path">Where it belongs within the job, in the form <see cref="RelativePath"/> describes; empty for a
/// job of one file</param>
/// <param name="Name">Its name, to show</param>
/// <param name="Length">Its length in bytes, or <c>null</c> while unknown</param>
/// <param name="Transferred">The bytes of it moved so far</param>
/// <param name="Done">Whether all of it was moved</param>
/// <param name="Location">Where it ended up once done, such as a file path or a URL, as
/// <see cref="TransferResult.Location"/> says</param>
public sealed record JobFile(string Path, string Name, long? Length, long Transferred, bool Done, string? Location = null);
