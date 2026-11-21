// Drives the job manager from the console, with Naqel's own engine and the plugins installed in its data folder:
//   Naqel.ConsoleHost <link or path> [<link or path> ...] [--to <folder>] [--jobs <n>] [--connections <n>] [--data <folder>]
//   Naqel.ConsoleHost plugins list [--data <folder>]
//   Naqel.ConsoleHost plugins search [<text>] --source <feed> [--data <folder>]
//   Naqel.ConsoleHost plugins install <id> [--version <v>] --source <feed> [--data <folder>]
//   Naqel.ConsoleHost plugins remove <id> [--data <folder>]
// Files and folders work without plugins; HTTP links need the HTTP plugin (Naqel.Http.Plugin), which the catalog
// installs from a feed, such as a folder holding its package. Installs and removals take effect on the next run.
// Every link becomes a job; at most --jobs of them run at once (default 2), each over --connections connections
// (default 4). The folder is a storage location named "downloads", so the jobs save to location://downloads/. While
// they run, press p to pause all, r to resume all, and q to quit; a plugin's question is asked on the console.

using System.Globalization;
using Naqel;
using Naqel.Files;
using Naqel.Manager;
using Naqel.Plugins;

var positional = new List<string>();
var folder = Path.Combine(Environment.CurrentDirectory, "downloads");
var data = Path.Combine(Environment.CurrentDirectory, "naqel-data");
var maxJobs = 2;
var connections = 4;
string? source = null;
string? version = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--to" when i + 1 < args.Length:
            folder = Path.GetFullPath(args[++i]);
            break;
        case "--data" when i + 1 < args.Length:
            data = Path.GetFullPath(args[++i]);
            break;
        case "--jobs" when i + 1 < args.Length:
            maxJobs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--connections" when i + 1 < args.Length:
            connections = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--source" when i + 1 < args.Length:
            source = args[++i];
            break;
        case "--version" when i + 1 < args.Length:
            version = args[++i];
            break;
        default:
            positional.Add(args[i]);
            break;
    }
}
var plugins = Path.Combine(data, "plugins");

if (positional is ["plugins", .. var command])
{
    return await PluginsAsync([.. command], new PluginCatalog(plugins, source == null ? [] : [Path.GetFullPath(source)]), version);
}
if (positional.Count == 0)
{
    Console.WriteLine("Usage: Naqel.ConsoleHost <link or path> [...] [--to <folder>] [--jobs <n>] [--connections <n>] [--data <folder>]");
    Console.WriteLine("       Naqel.ConsoleHost plugins list | search [<text>] --source <feed> | install <id> --source <feed> | remove <id>");
    return 2;
}

// What the host builds in: its storage locations, which name the sites jobs save to, and local files
var locations = new StorageLocations();
locations.Add("downloads", new FileStorage(folder));
var host = new PluginHost(new PluginHostOptions { PluginsDirectory = plugins, DataDirectory = Path.Combine(data, "plugin-data") });
host.AddBuiltIn("naqel.builtin", registry =>
{
    registry.AddSourceResolver(locations, ResolverRole.Site);
    registry.AddTargetResolver(locations, ResolverRole.Site);
    registry.AddSourceResolver(new FileSourceResolver(), ResolverRole.Protocol);
    registry.AddTargetResolver(new FileTargetResolver(), ResolverRole.Protocol);
});
var set = await host.LoadAsync();
foreach (var failure in set.Failures)
{
    Console.WriteLine($"Plugin {failure.Manifest?.Id ?? Path.GetFileName(failure.Directory)} didn't load: {failure.Reason}");
}
Console.WriteLine($"Plugins: {string.Join(", ", set.Plugins.Where(plugin => plugin.Directory != null).Select(plugin => $"{plugin.Manifest.Name} {plugin.Manifest.Version}").DefaultIfEmpty("none"))}");

var engineOptions = new NaqelEngineOptions { TransferOptions = new TransferOptions { MaxConnections = connections } };
set.ApplyTo(engineOptions);
var managerOptions = new JobManagerOptions { MaxActiveJobs = maxJobs, Locations = locations };
managerOptions.Engines.Add(new NaqelEngine(engineOptions));
managerOptions.Observers.Add(new ConsoleObserver());
set.ApplyTo(managerOptions);

await using var manager = new JobManager(managerOptions);
host.Bind(manager.Accounts);
var interactive = !Console.IsInputRedirected;
manager.Interactions.Asked += question => _ = AskAsync(manager, question, interactive);
await manager.StartAsync();

var destination = StorageLocations.UriOf("downloads");
foreach (var link in positional.Select(text => Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : new Uri(Path.GetFullPath(text))))
{
    try
    {
        await manager.AddAsync(link, destination);
    }
    catch (TransferException ex) when (ex.Kind == TransferErrorKind.Unsupported)
    {
        Console.WriteLine($"Can't add {link}: {ex.Message}; for HTTP links, install the HTTP plugin");
    }
}
if (manager.Jobs.Count == 0)
{
    return 1;
}
Console.WriteLine($"{manager.Jobs.Count} jobs, saving to {folder}. Keys: p pause all, r resume all, q quit.");

while (true)
{
    if (interactive && Console.KeyAvailable)
    {
        switch (Console.ReadKey(intercept: true).KeyChar)
        {
            case 'p':
                foreach (var job in manager.Jobs.Where(job => job.State is not (JobState.Paused or JobState.Completed or JobState.Failed or JobState.Finishing)))
                {
                    await manager.PauseAsync(job.Id);
                }
                break;
            case 'r':
                foreach (var job in manager.Jobs.Where(job => job.State is JobState.Paused or JobState.Failed))
                {
                    await manager.ResumeAsync(job.Id);
                }
                break;
            case 'q':
                return 0;
        }
    }

    var jobs = manager.Jobs;
    if (jobs.All(job => job.State is JobState.Completed or JobState.Failed))
    {
        return jobs.Any(job => job.State == JobState.Failed) ? 1 : 0;
    }
    foreach (var job in jobs.Where(job => job.State == JobState.Transferring))
    {
        Console.WriteLine($"  {Name(job),-30} {Percent(job.Progress),5} {job.Progress.Rate / 1024 / 1024,6:0.00} MB/s");
    }
    await Task.Delay(1_000);
}

static async Task<int> PluginsAsync(string[] command, PluginCatalog catalog, string? version)
{
    switch (command)
    {
        case ["list"]:
            foreach (var installed in catalog.Installed)
            {
                Console.WriteLine($"{installed.Id} {installed.Version}  {installed.Name}");
            }
            foreach (var change in catalog.Pending)
            {
                Console.WriteLine(change.IsRemoval ? $"{change.Id}: removed on the next run" : $"{change.Id} {change.Version}: installed on the next run");
            }
            return 0;
        case ["search", .. var text] when catalog.Sources.Count > 0:
            foreach (var entry in await catalog.SearchAsync(string.Join(' ', text)))
            {
                Console.WriteLine($"{entry.Id} {entry.Version}  {entry.Title}{(entry.Compatible ? "" : "  (built for another Naqel)")}");
            }
            return 0;
        case ["install", var id] when catalog.Sources.Count > 0:
            var manifest = await catalog.InstallAsync(id, version);
            Console.WriteLine($"{manifest.Name} {manifest.Version} is installed on the next run");
            return 0;
        case ["remove", var id]:
            catalog.Remove(id);
            Console.WriteLine($"{id} is removed on the next run");
            return 0;
        default:
            Console.WriteLine("Usage: plugins list | search [<text>] --source <feed> | install <id> [--version <v>] --source <feed> | remove <id>");
            return 2;
    }
}

// Asks a plugin's question on the console, or declines it when there's no one to answer
static async Task AskAsync(JobManager manager, PendingInteraction question, bool interactive)
{
    await Task.Yield();
    var request = question.Request;
    if (!interactive)
    {
        manager.Interactions.Answer(question.Id, InteractionAnswer.Declined);
        return;
    }
    Console.WriteLine();
    Console.WriteLine(request.Title + (request.Message is { } message ? $" ({message})" : ""));
    if (request.Url is { } url)
    {
        Console.WriteLine($"Open {url}");
    }
    if (request.Choices.Count > 0)
    {
        Console.WriteLine("Choices: " + string.Join(", ", request.Choices));
    }
    Console.Write("> ");
    var answer = Console.ReadLine();
    manager.Interactions.Answer(question.Id, string.IsNullOrEmpty(answer) ? InteractionAnswer.Declined : new InteractionAnswer(answer));
}

static string Name(JobInfo job) => job.Progress.Files is [var file] ? file.Name : job.Reference.Segments[^1];

static string Percent(JobSnapshot progress) => progress.Total is > 0 ? $"{(double)progress.Transferred / progress.Total.Value:P0}" : "?";

/// <summary>
/// Shows what happens to the jobs as it happens.
/// </summary>
internal sealed class ConsoleObserver : IJobObserver
{
    public Task OnEventAsync(JobEvent jobEvent, CancellationToken cancellationToken)
    {
        var job = jobEvent.Job;
        var what = jobEvent.Kind switch
        {
            JobEventKind.Added => $"added {job.Reference}",
            JobEventKind.Started => "started",
            JobEventKind.StateChanged => job.State.ToString().ToLowerInvariant(),
            JobEventKind.Completed => $"completed: {string.Join(", ", job.Progress.Files.Select(file => file.Location))}",
            JobEventKind.Failed => $"failed: {job.Error?.Kind}, {job.Error?.Message}",
            JobEventKind.Removed => "removed",
            _ => null
        };
        if (what != null)
        {
            Console.WriteLine($"{jobEvent.Time.ToLocalTime():HH:mm:ss} job {job.Id[..6]} {what}");
        }
        return Task.CompletedTask;
    }
}
