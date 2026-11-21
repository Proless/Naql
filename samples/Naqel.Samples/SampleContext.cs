namespace Naqel.Samples;

/// <param name="FileUri">The file used by the scenarios that download a whole file</param>
/// <param name="OutputDirectory">Where the downloaded files are saved</param>
/// <param name="CancellationToken">Canceled by Ctrl+C</param>
public sealed record SampleContext(Uri FileUri, string OutputDirectory, CancellationToken CancellationToken)
{
    /// <summary>A 10 MB test file from Scaleway; the server supports byte ranges and sends an ETag, but only over plain HTTP</summary>
    public static Uri DefaultFileUri { get; } = new("http://ping.online.net/10Mo.dat");

    public string FilePath(string name) => Path.Combine(OutputDirectory, name);
}
