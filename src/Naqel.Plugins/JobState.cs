namespace Naqel.Plugins;

/// <summary>
/// Where a job stands. An engine reports the states that describe its work; the manager adds
/// <see cref="Queued"/> and <see cref="Finishing"/>, which no engine reports.
/// </summary>
public enum JobState
{
    /// <summary>Waiting for its turn; the manager starts it when fewer jobs run than it allows.</summary>
    Queued,

    /// <summary>Finding out what to transfer, such as resolving a link into files or getting a torrent's metadata.</summary>
    Preparing,

    /// <summary>Checking data that's already there, such as a torrent's pieces after a restart.</summary>
    Checking,

    /// <summary>Moving bytes.</summary>
    Transferring,

    /// <summary>Waiting for the user to answer something, such as a password or a choice.</summary>
    NeedsAttention,

    /// <summary>Stopped by the user; it continues where it stopped when resumed.</summary>
    Paused,

    /// <summary>The content is complete and the job's completion steps are running.</summary>
    Finishing,

    /// <summary>Complete, and still uploading to others, such as a torrent that seeds.</summary>
    Seeding,

    /// <summary>Done.</summary>
    Completed,

    /// <summary>Stopped by an error, which the job's error describes; it can be resumed to try again.</summary>
    Failed
}
