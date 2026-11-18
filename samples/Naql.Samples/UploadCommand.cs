using Naql.Files;
using Naql.Http;

namespace Naql.Samples;

/// <summary>
/// Uploads a local file with PUT, POST or a multipart form, and shows the server's answer.
/// </summary>
public static class UploadCommand
{
    private const int MaxAnswerLength = 300;

    public static async Task<int> RunAsync(IReadOnlyList<string> arguments, Options options, CancellationToken cancellationToken)
    {
        if (arguments.Count is 0 or > 2)
        {
            throw new ArgumentException("Usage: upload <file> [<url>] [--method put|post|form] [--field <name>]");
        }

        var file = Path.GetFullPath(arguments[0]);
        var method = (options.Get("method") ?? "put").ToLowerInvariant();
        var url = arguments.Count == 2 ? arguments[1] : $"https://httpbin.org/{(method == "put" ? "put" : "post")}";
        var timeout = TimeSpan.FromSeconds(options.GetNumber("timeout", 30));
        var transferOptions = TransferOptions.Default with
        {
            MaxBytesPerSecond = (long)(options.GetNumber("limit", 0) * 1024),
            Retry = RetryPolicy.Default with { MaxRetries = (int)options.GetNumber("retries", 3) },
            ResponseTimeout = timeout,
            IdleTimeout = timeout
        };

        var target = method switch
        {
            "put" => HttpTarget.Put(url),
            "post" => HttpTarget.Post(url),
            "form" => HttpTarget.PostForm(url, options.Get("field") ?? "file"),
            _ => throw new ArgumentException($"Unknown method '{method}', use put, post or form")
        };

        string? answer = null;
        target = target.WithResponseHandler(async (response, ct) =>
        {
            // Show the start of the answer on one line
            var body = string.Join(' ', (await response.Content.ReadAsStringAsync(ct)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var shortened = body.Length > MaxAnswerLength ? body[..MaxAnswerLength] + "..." : body;
            answer = $"{(int)response.StatusCode} {response.ReasonPhrase}: {shortened}";
        });

        Console.WriteLine($"Uploading {file} to {url} ({method})");
        try
        {
            TransferResult result;
            using (var progress = new ConsoleProgress())
            {
                result = await Transfer.From(FileSource.At(file)).To(target).WithOptions(transferOptions).RunAsync(progress, cancellationToken);
            }

            var retries = result.Retries == 0 ? "" : $", {result.Retries} retries";
            Console.WriteLine($"Uploaded {Format.Bytes(result.Transferred)} in {result.Duration.TotalSeconds:0.0}s ({Format.Bytes(result.AverageRate)}/s{retries})");
            Console.WriteLine($"Server answered {answer}");
            return 0;
        }
        catch (TransferException ex)
        {
            Console.WriteLine($"FAILED: {Format.Error(ex)}");
            return 1;
        }
    }
}
