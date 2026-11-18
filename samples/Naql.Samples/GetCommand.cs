using Naql.Files;
using Naql.Http;
using System.Security.Cryptography;
using System.Text;

namespace Naql.Samples;

/// <summary>
/// Downloads the URLs given on the command line, or asks for them when none are given.
/// </summary>
public static class GetCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> urls, Options options, string outputDirectory, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(options.GetNumber("timeout", 30));
        var transferOptions = TransferOptions.Default with
        {
            // Content smaller than two parts of MinPartSize (1 MB by default) still uses one connection
            MaxConnections = (int)options.GetNumber("parallel", 1),
            MaxBytesPerSecond = (long)(options.GetNumber("limit", 0) * 1024),
            Retry = RetryPolicy.Default with { MaxRetries = (int)options.GetNumber("retries", 3) },
            ResponseTimeout = timeout,
            IdleTimeout = timeout,
            ExpectedChecksum = options.Get("sha256") is { } sha256 ? Checksum.Sha256(sha256) : null
        };

        if (urls.Count == 0)
        {
            urls = AskForUrls();
        }

        var failed = 0;
        foreach (var url in urls)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {url}");

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Console.WriteLine("FAILED: not an absolute http:// or https:// URL");
                failed++;
                continue;
            }

            try
            {
                await DownloadAsync(uri, outputDirectory, transferOptions, cancellationToken);
            }
            catch (TransferException ex)
            {
                Console.WriteLine($"FAILED: {Format.Error(ex)}");
                failed++;
            }
        }

        return failed == 0 ? 0 : 1;
    }

    private static async Task DownloadAsync(Uri uri, string outputDirectory, TransferOptions options, CancellationToken cancellationToken)
    {
        // The file is named from Content-Disposition, or else from the URL after redirects
        var target = FileTarget.InDirectory(outputDirectory);

        // One checkpoint per URL: the file name isn't known before the download starts, and the checkpoint finds the
        // partial file by itself. Running the same command again after Ctrl+C or a crash continues the download.
        var checkpoints = new FileCheckpointStore(Path.Combine(outputDirectory, ".naql", $"{UrlKey(uri)}{FileCheckpointStore.Extension}"));
        if (await checkpoints.LoadAsync(cancellationToken) is { } saved)
        {
            Console.WriteLine($"Found an unfinished download with {Format.Bytes(saved.Completed)}, continuing it unless the file changed");
        }

        TransferResult result;
        try
        {
            using var progress = new ConsoleProgress();
            result = await Transfer.From(HttpSource.Get(uri)).To(target).WithOptions(options).WithCheckpoints(checkpoints).RunAsync(progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (await checkpoints.LoadAsync(CancellationToken.None) is { } kept)
            {
                Console.WriteLine($"Kept {Format.Bytes(kept.Completed)}; run the same command again to continue");
            }
            throw;
        }

        var resumed = result.Resumed == 0 ? "" : $", continued after {Format.Bytes(result.Resumed)}";
        if (result.VerifiedChecksum is { } verified)
        {
            Console.WriteLine($"Verified {verified.Algorithm.Name} {verified.ToHex()[..16]}...");
        }
        var parts = result.Parts == 1 ? "one connection" : $"{result.Parts} parallel parts";
        var retries = result.Retries == 0 ? "" : $", {result.Retries} retries";
        Console.WriteLine($"Saved {Format.Bytes(result.Transferred)} in {result.Duration.TotalSeconds:0.0}s ({Format.Bytes(result.AverageRate)}/s, {parts}{retries}{resumed}) to {target.Path}");
    }

    /// <summary>
    /// A short, file-name-safe key for a URL.
    /// </summary>
    private static string UrlKey(Uri uri)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..16].ToLowerInvariant();
    }

    private static List<string> AskForUrls()
    {
        Console.WriteLine("Enter the URLs to download, one per line, and an empty line to start:");

        var urls = new List<string>();
        while (Console.ReadLine() is { Length: > 0 } line)
        {
            urls.Add(line.Trim());
        }
        return urls;
    }
}
