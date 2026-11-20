using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Naql;
using Naql.Files;
using Naql.Http;

namespace Naql.Soak
{
    /// <summary>
    /// Runs transfers against real servers for a set time and checks every result against a reference:
    /// <code>Naql.Soak [--minutes 10] [--url http://...] [--upload https://...] [--out dir]</code>
    /// It downloads in parallel, throttled, canceled and resumed from a checkpoint, verified by checksum, and with its
    /// speed limit and connections changed while it runs, and uploads with PUT. A failed transfer is counted, since public servers fail now and then; a transfer that
    /// succeeds with wrong bytes, or fails in an unexpected way, makes the exit code 1.
    /// </summary>
    public static class Program
    {
        private const string DefaultUrl = "http://ping.online.net/10Mo.dat";
        private const string DefaultUploadUrl = "https://httpbin.org/put";

        private static readonly Random Random = new Random();

        public static async Task<int> Main(string[] args)
        {
            var minutes = double.Parse(Option(args, "--minutes") ?? "10", System.Globalization.CultureInfo.InvariantCulture);
            var url = new Uri(Option(args, "--url") ?? DefaultUrl);
            var uploadUrl = new Uri(Option(args, "--upload") ?? DefaultUploadUrl);
            var directory = Option(args, "--out") ?? Path.Combine(Path.GetTempPath(), "Naql.Soak");
            Directory.CreateDirectory(directory);

            Console.WriteLine($"Naql.Soak on {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, for {minutes} minutes");
            Console.WriteLine($"Downloading {url}, uploading to {uploadUrl}, files in {directory}");

            var reference = Path.Combine(directory, "reference.bin");
            await Transfer.From(HttpSource.Get(url)).To(FileTarget.At(reference)).RunAsync();
            var expected = Sha256(reference);
            Console.WriteLine($"Reference: {new FileInfo(reference).Length} bytes, SHA-256 {expected}");

            var scenarios = new (string Name, Func<Uri, Uri, string, string, Task<string>> Run)[]
            {
                ("parallel", ParallelAsync),
                ("throttled", ThrottledAsync),
                ("resumed", ResumedAsync),
                ("checksum", ChecksumAsync),
                ("live", LiveAsync),
                ("upload", UploadAsync)
            };
            var results = scenarios.ToDictionary(scenario => scenario.Name, _ => new Tally());
            var clock = Stopwatch.StartNew();
            var round = 0;
            while (clock.Elapsed < TimeSpan.FromMinutes(minutes))
            {
                var (name, run) = scenarios[round++ % scenarios.Length];
                var tally = results[name];
                var started = clock.Elapsed;
                try
                {
                    var note = await run(url, uploadUrl, directory, expected);
                    tally.Passed++;
                    Console.WriteLine($"{clock.Elapsed:hh\\:mm\\:ss} {name,-10} ok    {(clock.Elapsed - started).TotalSeconds,5:0.0}s {note}");
                }
                catch (CorruptException ex)
                {
                    tally.Corrupt++;
                    Console.WriteLine($"{clock.Elapsed:hh\\:mm\\:ss} {name,-10} WRONG {ex.Message}");
                }
                catch (TransferFailedException ex)
                {
                    tally.Failed++;
                    Console.WriteLine($"{clock.Elapsed:hh\\:mm\\:ss} {name,-10} fail  {(ex.IsTransient ? "transient" : "permanent")} {ex.Kind} on the {ex.Side} side: {ex.Message}");
                }
                catch (Exception ex)
                {
                    tally.Unexpected++;
                    Console.WriteLine($"{clock.Elapsed:hh\\:mm\\:ss} {name,-10} UNEXPECTED {ex}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"{"scenario",-10} {"ok",5} {"failed",7} {"wrong",6} {"unexpected",11}");
            foreach (var pair in results)
            {
                Console.WriteLine($"{pair.Key,-10} {pair.Value.Passed,5} {pair.Value.Failed,7} {pair.Value.Corrupt,6} {pair.Value.Unexpected,11}");
            }
            var bad = results.Values.Sum(tally => tally.Corrupt + tally.Unexpected);
            Console.WriteLine(bad == 0 ? "No wrong results." : $"{bad} wrong or unexpected results.");
            return bad == 0 ? 0 : 1;
        }

        private static async Task<string> ParallelAsync(Uri url, Uri uploadUrl, string directory, string expected)
        {
            var path = Path.Combine(directory, "parallel.bin");
            var result = await Transfer.From(HttpSource.Get(url)).To(FileTarget.At(path)).WithMaxConnections(4).RunAsync();
            Check(path, expected);
            return $"{result.Parts} parts, {result.Retries} retries";
        }

        private static async Task<string> ThrottledAsync(Uri url, Uri uploadUrl, string directory, string expected)
        {
            const long limit = 4 * 1024 * 1024;
            var path = Path.Combine(directory, "throttled.bin");
            var result = await Transfer.From(HttpSource.Get(url)).To(FileTarget.At(path)).WithMaxConnections(4).WithMaxBytesPerSecond(limit).RunAsync();
            Check(path, expected);
            // The limiter allows a burst of up to a second's worth at the start
            var allowed = limit * (result.Duration.TotalSeconds + 1);
            if (result.Transferred > allowed)
            {
                throw new CorruptException($"moved {result.Transferred} bytes in {result.Duration.TotalSeconds:0.0}s, more than the limit allows");
            }
            return $"{result.AverageRate / 1024 / 1024:0.0} MB/s under a limit of {limit / 1024 / 1024} MB/s";
        }

        private static async Task<string> ResumedAsync(Uri url, Uri uploadUrl, string directory, string expected)
        {
            var path = Path.Combine(directory, "resumed.bin");
            var store = FileCheckpointStore.For(path);
            await store.DeleteAsync(CancellationToken.None);

            var cancelAfter = Random.Next(200, 2_000);
            using (var cts = new CancellationTokenSource(cancelAfter))
            {
                try
                {
                    await Transfer.From(HttpSource.Get(url)).To(FileTarget.At(path)).WithMaxConnections(4).WithCheckpointStore(store).RunAsync(cancellationToken: cts.Token);
                    Check(path, expected);
                    return $"finished within {cancelAfter} ms, before the cancellation";
                }
                catch (OperationCanceledException)
                {
                    // Expected: continue below
                }
            }

            var result = await Transfer.From(HttpSource.Get(url)).To(FileTarget.At(path)).WithMaxConnections(4).WithCheckpointStore(store).RunAsync();
            Check(path, expected);
            if (File.Exists(store.Path))
            {
                throw new CorruptException("the checkpoint is still there after the transfer completed");
            }
            return $"canceled after {cancelAfter} ms, continued after {result.Resumed} bytes";
        }

        private static async Task<string> ChecksumAsync(Uri url, Uri uploadUrl, string directory, string expected)
        {
            var path = Path.Combine(directory, "checksum.bin");
            var result = await Transfer.From(HttpSource.Get(url)).To(FileTarget.At(path)).WithMaxConnections(4).WithExpectedChecksum(Checksum.Sha256(expected)).RunAsync();
            Check(path, expected);
            if (result.VerifiedChecksum == null)
            {
                throw new CorruptException("the checksum wasn't verified");
            }
            return $"verified in {result.Parts} parts";
        }

        private static async Task<string> LiveAsync(Uri url, Uri uploadUrl, string directory, string expected)
        {
            // Starts slow on one connection; once a megabyte arrived, the limit is lifted and the running part is split
            // for three more connections
            var path = Path.Combine(directory, "live.bin");
            var operation = Transfer.From(HttpSource.Get(url)).To(FileTarget.At(path)).WithMaxBytesPerSecond(1024 * 1024).Build();
            var changed = 0;
            var progress = new Progress<TransferProgress>(p =>
            {
                if (p.Transferred > 1024 * 1024 && Interlocked.Exchange(ref changed, 1) == 0)
                {
                    operation.SetMaxBytesPerSecond(0);
                    operation.SetMaxConnections(4);
                }
            });
            var result = await operation.RunAsync(progress);
            Check(path, expected);
            return changed == 1 ? $"{result.Parts} parts after raising the connections, {result.Retries} retries" : "finished before the change";
        }

        private static async Task<string> UploadAsync(Uri url, Uri uploadUrl, string directory, string expected)
        {
            var content = new byte[256 * 1024];
            Random.NextBytes(content);
            var path = Path.Combine(directory, "upload.bin");
            File.WriteAllBytes(path, content);

            string? answer = null;
            var target = HttpTarget.Put(uploadUrl).WithResponseHandler(async (response, cancellationToken) =>
            {
                answer = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            });
            await Transfer.From(FileSource.At(path)).To(target).RunAsync();

            // httpbin echoes the request, with the body base64-encoded
            var sent = Convert.ToBase64String(content);
            if (answer == null || !answer.Contains(sent))
            {
                throw new CorruptException("the server didn't receive the uploaded bytes");
            }
            return $"{content.Length} bytes arrived intact";
        }

        private static void Check(string path, string expected)
        {
            var actual = Sha256(path);
            if (actual != expected)
            {
                throw new CorruptException($"{Path.GetFileName(path)} has SHA-256 {actual}, not {expected}");
            }
        }

        private static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private static string? Option(string[] args, string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private sealed class Tally
        {
            public int Passed;
            public int Failed;
            public int Corrupt;
            public int Unexpected;
        }

        private sealed class CorruptException : Exception
        {
            public CorruptException(string message) : base(message)
            {
            }
        }
    }
}
