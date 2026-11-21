// Drives the job manager from the console, with Naqel's own engine for HTTP and files:
//   Naqel.ConsoleHost <link or path> [<link or path> ...] [--to <folder>] [--jobs <n>] [--connections <n>]
// Every link becomes a job; at most --jobs of them run at once (default 2), each over --connections connections
// (default 4). The folder is a storage location named "downloads", so the jobs save to location://downloads/. While
// they run, press p to pause all, r to resume all, and q to quit.

using System.Globalization;
using Naqel;
using Naqel.Files;
using Naqel.Http;
using Naqel.Manager;
using Naqel.Plugins;

var links = new List<Uri>();
var folder = Path.Combine(Environment.CurrentDirectory, "downloads");
var maxJobs = 2;
var connections = 4;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--to" when i + 1 < args.Length:
            folder = Path.GetFullPath(args[++i]);
            break;
        case "--jobs" when i + 1 < args.Length:
            maxJobs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--connections" when i + 1 < args.Length:
            connections = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        default:
            links.Add(Uri.TryCreate(args[i], UriKind.Absolute, out var uri) ? uri : new Uri(Path.GetFullPath(args[i])));
            break;
    }
}
if (links.Count == 0)
{
    Console.WriteLine("Usage: Naqel.ConsoleHost <link or path> [...] [--to <folder>] [--jobs <n>] [--connections <n>]");
    return 2;
}

var locations = new StorageLocations();
locations.Add("downloads", new FileStorage(folder));
var engineOptions = new NaqelEngineOptions { TransferOptions = new TransferOptions { MaxConnections = connections } };
engineOptions.SourceResolvers.Add(locations);
engineOptions.SourceResolvers.Add(new HttpSourceResolver(describe: false));
engineOptions.SourceResolvers.Add(new FileSourceResolver());
engineOptions.TargetResolvers.Add(locations);
engineOptions.TargetResolvers.Add(new FileTargetResolver());
var managerOptions = new JobManagerOptions { MaxActiveJobs = maxJobs, Locations = locations };
managerOptions.Engines.Add(new NaqelEngine(engineOptions));
managerOptions.Observers.Add(new ConsoleObserver());

await using var manager = new JobManager(managerOptions);
await manager.StartAsync();
var destination = StorageLocations.UriOf("downloads");
foreach (var link in links)
{
    await manager.AddAsync(link, destination);
}
Console.WriteLine($"{links.Count} jobs, saving to {folder}. Keys: p pause all, r resume all, q quit.");

var interactive = !Console.IsInputRedirected;
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
