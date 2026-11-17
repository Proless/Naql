using Naql.Files;
using Naql.Http;
using Naql.Streams;

namespace Naql.Samples;

public static class Scenarios
{
    private static readonly Dictionary<Uri, string> ReferenceHashes = new();

    /// <summary>
    /// The simplest use: download a file with progress.
    /// </summary>
    public static async Task SimpleAsync(SampleContext context)
    {
        var path = context.FilePath("simple.bin");

        TransferResult result;
        using (var progress = new ConsoleProgress())
        {
            result = await Transfer
                .From(HttpSource.Get(context.FileUri))
                .To(FileTarget.At(path))
                .RunAsync(progress, context.CancellationToken);
        }

        Console.WriteLine($"Saved {Format.Bytes(result.Transferred)} in {result.Duration.TotalSeconds:0.0}s to {path}");
    }

    /// <summary>
    /// Downloads a file over several connections at once. The engine asks the server for the size and range support,
    /// and splits the file into parts when it can.
    /// </summary>
    public static async Task ParallelAsync(SampleContext context)
    {
        var path = context.FilePath("parallel.bin");

        TransferResult result;
        using (var progress = new ConsoleProgress())
        {
            result = await Transfer
                .From(HttpSource.Get(context.FileUri))
                .To(FileTarget.At(path))
                .WithMaxConnections(4)
                .RunAsync(progress, context.CancellationToken);
        }

        Console.WriteLine($"Downloaded {Format.Bytes(result.Transferred)} in {result.Parts} parts in {result.Duration.TotalSeconds:0.0}s");
        if (result.Parts == 1)
        {
            Console.WriteLine("The server doesn't support ranges, or the file is too small to split");
        }

        await VerifyAgainstSingleDownloadAsync(context, path);
    }

    /// <summary>
    /// Cancels a parallel download halfway and saves its checkpoint to a file, then continues it from that file the way
    /// a restarted app would: only the missing bytes are downloaded.
    /// </summary>
    public static async Task ResumeAsync(SampleContext context)
    {
        var path = context.FilePath("resume.bin");
        var checkpointPath = path + ".checkpoint";
        using var halfway = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

        var operation = Transfer.From(HttpSource.Get(context.FileUri)).To(FileTarget.At(path)).WithMaxConnections(4).Build();
        using (var console = new ConsoleProgress())
        {
            var progress = new CallbackProgress(report =>
            {
                console.Report(report);
                if (report.Fraction >= 0.5)
                {
                    halfway.Cancel();
                }
            });

            try
            {
                await operation.RunAsync(progress, halfway.Token);
                throw new InvalidOperationException("The download finished before it was canceled");
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
            {
            }
        }

        var checkpoint = operation.GetCheckpoint() ?? throw new InvalidOperationException("The server doesn't allow continuing the download");
        await File.WriteAllTextAsync(checkpointPath, checkpoint.Serialize(), context.CancellationToken);
        Console.WriteLine($"Canceled after {Format.Bytes(checkpoint.Completed)} in {checkpoint.Parts.Count} parts; saved the checkpoint to {Path.GetFileName(checkpointPath)}");

        // Later, maybe after a restart: load the checkpoint and continue
        var saved = TransferCheckpoint.Deserialize(await File.ReadAllTextAsync(checkpointPath, context.CancellationToken));
        TransferResult result;
        using (var progress = new ConsoleProgress())
        {
            result = await Transfer
                .From(HttpSource.Get(context.FileUri))
                .To(FileTarget.At(path))
                .WithMaxConnections(4)
                .ResumeFrom(saved)
                .RunAsync(progress, context.CancellationToken);
        }
        File.Delete(checkpointPath);

        Console.WriteLine($"Resumed with {Format.Bytes(result.Resumed)} already there and downloaded the other {Format.Bytes(result.Transferred - result.Resumed)}");
        if (result.Resumed == 0)
        {
            throw new InvalidOperationException("The download started over instead of continuing");
        }

        await VerifyAgainstSingleDownloadAsync(context, path);
    }

    /// <summary>
    /// Limits the download speed for the whole transfer, across all its parts.
    /// </summary>
    public static async Task ThrottleAsync(SampleContext context)
    {
        const long limit = 2 * 1024 * 1024;

        TransferResult result;
        using (var progress = new ConsoleProgress())
        {
            result = await Transfer
                .From(HttpSource.Get(context.FileUri))
                .To(FileTarget.At(context.FilePath("throttle.bin")))
                .WithMaxConnections(4)
                .WithMaxRate(limit)
                .RunAsync(progress, context.CancellationToken);
        }

        Console.WriteLine($"Limit {Format.Bytes(limit)}/s, measured {Format.Bytes(result.AverageRate)}/s over {result.Duration.TotalSeconds:0.0}s in {result.Parts} parts");
        if (result.AverageRate > limit * 1.25)
        {
            throw new InvalidOperationException("The download was faster than the limit");
        }
    }

    /// <summary>
    /// Sends HTTP basic authentication credentials. httpbin.org accepts the user and password given in its URL.
    /// </summary>
    public static async Task AuthAsync(SampleContext context)
    {
        var uri = new Uri("https://httpbin.org/basic-auth/sample-user/sample-password");
        var path = context.FilePath("auth.json");

        var transport = new HttpTransport(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, disposeClient: true)
        {
            ConfigureRequest = HttpTransport.BasicAuthentication("sample-user", "sample-password")
        };
        using (transport)
        {
            await Transfer.From(transport.Get(uri)).To(FileTarget.At(path)).RunAsync(cancellationToken: context.CancellationToken);
        }

        Console.WriteLine($"With credentials: {File.ReadAllText(path).ReplaceLineEndings(" ")}");

        await ExpectFailureAsync("Without credentials",
            () => Transfer.From(HttpSource.Get(uri)).To(new StreamTarget(Stream.Null)).RunAsync(cancellationToken: context.CancellationToken));
    }

    /// <summary>
    /// Uploads a generated file to httpbin.org, which echoes what it received.
    /// </summary>
    public static async Task UploadAsync(SampleContext context)
    {
        var path = context.FilePath("upload.bin");
        var content = new byte[256 * 1024];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(path, content, context.CancellationToken);

        string? receivedLength = null;
        var target = HttpTarget.Put("https://httpbin.org/put").WithResponseHandler(async (response, ct) =>
        {
            using var answer = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            receivedLength = answer.RootElement.GetProperty("headers").GetProperty("Content-Length").GetString();
        });

        var result = await Transfer.From(FileSource.At(path)).To(target).RunAsync(cancellationToken: context.CancellationToken);

        Console.WriteLine($"Uploaded {Format.Bytes(result.Transferred)} in {result.Duration.TotalSeconds:0.0}s, the server received Content-Length {receivedLength}");
        if (receivedLength != content.Length.ToString())
        {
            throw new InvalidOperationException("The server received a different length");
        }
    }

    /// <summary>
    /// Shows what the caller sees when a transfer fails.
    /// </summary>
    public static async Task ErrorsAsync(SampleContext context)
    {
        var quick = TransferOptions.Default with
        {
            ResponseTimeout = TimeSpan.FromSeconds(2),
            IdleTimeout = TimeSpan.FromSeconds(2),
            Retry = RetryPolicy.Default with { MaxRetries = 1 }
        };

        Task Download(string url, TransferOptions options) =>
            Transfer.From(HttpSource.Get(url)).To(new StreamTarget(Stream.Null)).WithOptions(options).RunAsync(new RetryPrinter(), context.CancellationToken);

        await ExpectFailureAsync("404 Not Found", () => Download("https://httpbin.org/status/404", quick));

        // httpbin sends a byte about every 4.5 seconds, longer than the idle timeout
        await ExpectFailureAsync("Stalled response", () => Download("https://httpbin.org/drip?numbytes=2&duration=9&delay=0", quick));

        // The server takes 10 seconds to send the response headers
        await ExpectFailureAsync("Slow response headers", () => Download("https://httpbin.org/delay/10", quick with { Retry = RetryPolicy.None }));

        // This server ignores Range headers and always sends the whole file: a transfer just uses one connection,
        // but asking the source for a range directly fails
        await ExpectFailureAsync("Range request to a server without range support", async () =>
        {
            using var read = await HttpSource.Get("https://speed.cloudflare.com/__down?bytes=100000").OpenReadAsync(new ReadRequest(new ByteRange(0, 1_000)), context.CancellationToken);
        });
    }

    private static async Task ExpectFailureAsync(string name, Func<Task> action)
    {
        var started = DateTime.UtcNow;
        try
        {
            await action();
        }
        catch (TransferException ex)
        {
            var cause = ex.InnerException is null ? "" : $" (cause: {ex.InnerException.GetType().Name})";
            var transient = ex.IsTransient ? "transient" : "permanent";
            Console.WriteLine($"{name}: {transient} {ex.Kind} error on the {ex.Side.ToString().ToLowerInvariant()} side after {(DateTime.UtcNow - started).TotalSeconds:0.0}s: {ex.Message}{cause}");
            return;
        }

        throw new InvalidOperationException($"{name}: expected a TransferException");
    }

    /// <summary>
    /// Compares a file with a single-connection download of the same URL, so parallel downloads are known to be byte-identical.
    /// </summary>
    private static async Task VerifyAgainstSingleDownloadAsync(SampleContext context, string path)
    {
        if (!ReferenceHashes.TryGetValue(context.FileUri, out var expected))
        {
            var referencePath = context.FilePath("reference.bin");
            Console.WriteLine("Downloading a reference copy over one connection to compare with");
            await Transfer.From(HttpSource.Get(context.FileUri)).To(FileTarget.At(referencePath)).RunAsync(cancellationToken: context.CancellationToken);

            expected = Format.Sha256(referencePath);
            ReferenceHashes[context.FileUri] = expected;
        }

        var actual = Format.Sha256(path);
        if (actual != expected)
        {
            throw new InvalidOperationException($"The file differs from a single download: SHA-256 {actual[..16]} instead of {expected[..16]}");
        }

        Console.WriteLine($"Verified: identical to a single download (SHA-256 {actual[..16]}...)");
    }

    /// <summary>
    /// Passes every report to a callback.
    /// </summary>
    private sealed class CallbackProgress(Action<TransferProgress> callback) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => callback(value);
    }

    /// <summary>
    /// Prints each new error a transfer retries after.
    /// </summary>
    private sealed class RetryPrinter : IProgress<TransferProgress>
    {
        private Exception? _lastError;

        public void Report(TransferProgress value)
        {
            if (value.LastError is { } error && error != _lastError)
            {
                _lastError = error;
                Console.WriteLine($"  attempt failed ({error.Message}), retrying");
            }
        }
    }
}
