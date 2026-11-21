using Naqel.Samples;

// Demonstrates the library against real servers:
//   Naqel.Samples get <url> [<url> ...] [--parallel <n>] [--limit <KB/s>] [--retries <n>] [--timeout <s>] [--out <dir>]
//   Naqel.Samples upload <file> [<url>] [--method put|post|form] [--field <name>] [--limit <KB/s>] [--retries <n>] [--timeout <s>]
//   Naqel.Samples <scenario|all> [--url <file url>] [--out <dir>]

var scenarios = new (string Name, string Description, Func<SampleContext, Task> Run)[]
{
    ("simple", "Download a file and show progress", Scenarios.SimpleAsync),
    ("parallel", "Download a file in parallel parts and verify it against a single download", Scenarios.ParallelAsync),
    ("resume", "Cancel a parallel download halfway, save its checkpoint, and continue it from the checkpoint", Scenarios.ResumeAsync),
    ("throttle", "Limit the speed of a parallel download", Scenarios.ThrottleAsync),
    ("auth", "Download with HTTP basic authentication", Scenarios.AuthAsync),
    ("upload", "Upload a file and check what the server received", Scenarios.UploadAsync),
    ("errors", "Show how failures, timeouts and servers without range support are reported", Scenarios.ErrorsAsync),
};

Options options;
try
{
    options = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

var command = options.Positionals.FirstOrDefault();
if (command is null or "help" || options.Get("help") != null)
{
    PrintUsage();
    return 0;
}

// Ctrl+C cancels the running transfer instead of killing the process
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var isUpload = command.Equals("upload", StringComparison.OrdinalIgnoreCase);
var outputDirectory = Path.GetFullPath(options.Get("out") ?? Path.Combine(Path.GetTempPath(), "Naqel.Samples"));
if (!isUpload)
{
    Directory.CreateDirectory(outputDirectory);
    Console.WriteLine($"Files are saved to {outputDirectory}");
}

try
{
    if (isUpload)
    {
        return await UploadCommand.RunAsync(options.Positionals.Skip(1).ToList(), options, cts.Token);
    }

    if (command.Equals("get", StringComparison.OrdinalIgnoreCase))
    {
        return await GetCommand.RunAsync(options.Positionals.Skip(1).ToList(), options, outputDirectory, cts.Token);
    }

    return await RunScenariosAsync(command);
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.WriteLine();
    Console.WriteLine("Canceled by Ctrl+C");
    return 130;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

async Task<int> RunScenariosAsync(string name)
{
    var selected = name.Equals("all", StringComparison.OrdinalIgnoreCase)
        ? scenarios
        : scenarios.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();

    if (selected.Length == 0)
    {
        Console.Error.WriteLine($"Unknown command '{name}', run with 'help' to list the commands.");
        return 2;
    }

    var url = options.Get("url");
    var context = new SampleContext(url != null ? new Uri(url) : SampleContext.DefaultFileUri, outputDirectory, cts.Token);

    var failed = 0;
    foreach (var scenario in selected)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {scenario.Name}: {scenario.Description}");

        try
        {
            await scenario.Run(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cts.IsCancellationRequested)
        {
            failed++;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
        }
    }

    Console.WriteLine();
    Console.WriteLine(failed == 0 ? "All scenarios succeeded." : $"{failed} scenario(s) failed.");
    return failed == 0 ? 0 : 1;
}

void PrintUsage()
{
    Console.WriteLine("Download your own files:");
    Console.WriteLine("  Naqel.Samples get <url> [<url> ...] [options]");
    Console.WriteLine("  Naqel.Samples get                         asks for the URLs");
    Console.WriteLine();
    Console.WriteLine("    --parallel <n>   download in n parallel ranges when the server supports it (default 1)");
    Console.WriteLine("    --limit <KB/s>   limit the download speed (default unlimited)");
    Console.WriteLine("    --retries <n>    retries after a failed attempt (default 3)");
    Console.WriteLine("    --sha256 <hex>   fail unless the file has this SHA-256 (for one URL)");
    Console.WriteLine("    --timeout <s>    timeout for each response and read (default 30)");
    Console.WriteLine("    --out <dir>      where to save the files (default: a Naqel.Samples folder in the temp directory)");
    Console.WriteLine();
    Console.WriteLine("Upload a file:");
    Console.WriteLine("  Naqel.Samples upload <file> [<url>] [options]");
    Console.WriteLine();
    Console.WriteLine("    <url>            where to upload (default: httpbin.org, which echoes the upload)");
    Console.WriteLine("    --method <m>     put (default), post for a plain body, or form for a multipart form");
    Console.WriteLine("    --field <name>   the form field of the file with --method form (default file)");
    Console.WriteLine("    --limit, --retries and --timeout work as for get");
    Console.WriteLine();
    Console.WriteLine("Run the demo scenarios against public test servers:");
    Console.WriteLine("  Naqel.Samples <scenario|all> [--url <file url>] [--out <dir>]");
    Console.WriteLine();
    foreach (var scenario in scenarios)
    {
        Console.WriteLine($"    {scenario.Name,-10} {scenario.Description}");
    }
    Console.WriteLine();
    Console.WriteLine($"    --url <url>  the file used by simple, parallel, resume and throttle (default {SampleContext.DefaultFileUri})");
}
