namespace Naqel.Plugins;

/// <summary>
/// What a completion step gets: the job, and a way to add to its data.
/// </summary>
public interface ICompletionContext
{
    /// <summary>
    /// The job, with its files and where they ended up in <see cref="JobSnapshot.Files"/> of its progress.
    /// </summary>
    JobInfo Job { get; }

    /// <summary>
    /// Keeps <paramref name="value"/> under <paramref name="key"/> in the job's data, or deletes it when it's
    /// <c>null</c>. The data is kept when the step ends, also when it fails.
    /// </summary>
    void SetData(string key, string? value);
}
