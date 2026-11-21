using System.Runtime.Loader;
using System.Text.Json;
using Naqel.Files;
using Naqel.Plugins;

namespace Naqel.Manager.Tests;

/// <summary>
/// Loading plugins from a folder: each in a load context of its own, with the contracts shared with Naqel, in role
/// order, with settings, and with the plugins that can't load left out and explained.
/// </summary>
public sealed class PluginHostTests : IAsyncDisposable
{
    private static readonly string TestPlugin = Path.Combine(AppContext.BaseDirectory, "test-plugin", "Naqel.TestPlugin.dll");

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "NaqelPluginTests", Guid.NewGuid().ToString("N"));
    private readonly List<JobManager> _managers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Plugins => Path.Combine(_temp, "plugins");

    private string Data => Path.Combine(_temp, "data");

    public async ValueTask DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            await manager.DisposeAsync();
        }
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A loaded plugin's assembly stays open until the process ends
        }
    }

    private string Install(string folder, int contract = PluginContract.Version, string id = "Naqel.TestPlugin", string assembly = "Naqel.TestPlugin.dll")
    {
        var directory = Path.Combine(Plugins, folder);
        Directory.CreateDirectory(directory);
        File.Copy(TestPlugin, Path.Combine(directory, "Naqel.TestPlugin.dll"));
        // A careless package carries its own copies of the contracts; Naqel must use its own anyway
        File.Copy(typeof(ISourceResolver).Assembly.Location, Path.Combine(directory, "Naqel.Core.dll"));
        File.Copy(typeof(INaqelPlugin).Assembly.Location, Path.Combine(directory, "Naqel.Plugins.dll"));
        var manifest = new PluginManifest { Id = id, Name = "Test plugin", Version = "1.0.0", Contract = contract, Assembly = assembly };
        File.WriteAllText(Path.Combine(directory, PluginManifest.FileName), JsonSerializer.Serialize(manifest));
        return directory;
    }

    private PluginHost Host(ISettingsStore? settings = null) =>
        new(new PluginHostOptions { PluginsDirectory = Plugins, DataDirectory = Data, Settings = settings ?? new MemorySettingsStore() });

    private async Task<JobManager> StartAsync(PluginSet set, PluginHost host)
    {
        var engineOptions = new NaqelEngineOptions();
        set.ApplyTo(engineOptions);
        engineOptions.TargetResolvers.Add(new FileTargetResolver());
        var options = new JobManagerOptions();
        options.Engines.Add(new NaqelEngine(engineOptions));
        set.ApplyTo(options);
        var manager = new JobManager(options);
        _managers.Add(manager);
        host.Bind(manager.Accounts);
        await manager.StartAsync(Token);
        return manager;
    }

    private static async Task<JobInfo> CompletedAsync(JobManager manager, string jobId)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var job = manager.Find(jobId)!;
            if (job.State is JobState.Completed or JobState.Failed)
            {
                Assert.True(job.State == JobState.Completed, job.Error?.Message);
                return job;
            }
            await Task.Delay(5, timeout.Token);
        }
    }

    private Uri Output => new(Path.Combine(_temp, "out") + Path.DirectorySeparatorChar);

    [Fact]
    public async Task A_plugin_loads_in_a_context_of_its_own_and_its_resolver_and_observer_work_in_jobs()
    {
        Install("test-plugin");
        var host = Host();

        var set = await host.LoadAsync(Token);
        var manager = await StartAsync(set, host);
        var job = await manager.AddAsync(new Uri("greeting:world"), Output, cancellationToken: Token);
        await CompletedAsync(manager, job.Id);

        var plugin = Assert.Single(set.Plugins);
        Assert.Empty(set.Failures);
        Assert.Equal("Naqel.TestPlugin", plugin.Manifest.Id);
        var resolver = Assert.Single(set.SourceResolvers);
        // Its own context, but the contracts are Naqel's: the resolver is the ISourceResolver Naqel knows
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(resolver.GetType().Assembly));
        Assert.Same(typeof(ISourceResolver).Assembly, resolver.GetType().GetInterface(nameof(ISourceResolver))!.Assembly);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_temp, "out", "world.txt")));
        var events = Path.Combine(Data, "Naqel.TestPlugin", "events.txt");
        await EventuallyAsync(() => ReadShared(events).Contains("Completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sites_resolver_comes_before_a_protocols_whatever_registered_first()
    {
        Install("test-plugin");
        var host = Host();
        var builtIn = new FileSourceResolver();
        host.AddBuiltIn("naqel.builtin", registry => registry.AddSourceResolver(builtIn, ResolverRole.Protocol));

        var set = await host.LoadAsync(Token);

        Assert.Equal(2, set.SourceResolvers.Count);
        Assert.Equal("GreetingResolver", set.SourceResolvers[0].GetType().Name);
        Assert.Same(builtIn, set.SourceResolvers[1]);
        Assert.Equal(["naqel.builtin", "Naqel.TestPlugin"], set.Plugins.Select(plugin => plugin.Manifest.Id));
    }

    [Fact]
    public async Task A_plugin_reads_its_settings_and_hears_when_they_change()
    {
        Install("test-plugin");
        var settings = new MemorySettingsStore();
        await settings.SetAsync("Naqel.TestPlugin", "greeting", "hi", Token);
        var host = Host(settings);

        var set = await host.LoadAsync(Token);
        var plugin = Assert.Single(set.Plugins);
        var changed = new List<string>();
        plugin.Settings.Changed += changed.Add;
        var manager = await StartAsync(set, host);
        var first = await manager.AddAsync(new Uri("greeting:first"), Output, cancellationToken: Token);
        await CompletedAsync(manager, first.Id);
        await host.SetSettingAsync("Naqel.TestPlugin", "greeting", "hey", Token);
        var second = await manager.AddAsync(new Uri("greeting:second"), Output, cancellationToken: Token);
        await CompletedAsync(manager, second.Id);

        Assert.Equal(["greeting", "fail"], plugin.SettingDefinitions.Select(definition => definition.Key));
        Assert.Equal("hi", File.ReadAllText(Path.Combine(_temp, "out", "first.txt")));
        Assert.Equal("hey", File.ReadAllText(Path.Combine(_temp, "out", "second.txt")));
        Assert.Equal(["greeting"], changed);
        Assert.Equal("hey", (await settings.GetAllAsync("Naqel.TestPlugin", Token))["greeting"]);
    }

    [Fact]
    public async Task Plugins_that_cant_load_are_left_out_with_a_reason_and_the_others_load()
    {
        Install("a-good");
        Install("b-newer", contract: PluginContract.Version + 1, id: "Newer");
        Install("c-missing", id: "Missing", assembly: "Nothing.dll");
        Install("d-outside", id: "Outside", assembly: "../a-good/Naqel.TestPlugin.dll");
        Directory.CreateDirectory(Path.Combine(Plugins, "e-no-manifest"));
        File.WriteAllText(Path.Combine(Install("f-broken", id: "Broken"), PluginManifest.FileName), "{ not json");
        Install("g-failing", id: "Failing");
        var settings = new MemorySettingsStore();
        await settings.SetAsync("Failing", "fail", "true", Token);
        Directory.CreateDirectory(Path.Combine(Plugins, ".pending"));

        var set = await Host(settings).LoadAsync(Token);

        Assert.Equal(["Naqel.TestPlugin"], set.Plugins.Select(plugin => plugin.Manifest.Id));
        Assert.Single(set.SourceResolvers);
        var reasons = set.Failures.ToDictionary(failure => Path.GetFileName(failure.Directory), failure => failure.Reason);
        Assert.Equal(["b-newer", "c-missing", "d-outside", "e-no-manifest", "f-broken", "g-failing"], reasons.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("version", reasons["b-newer"], StringComparison.Ordinal);
        Assert.Contains("Nothing.dll", reasons["c-missing"], StringComparison.Ordinal);
        Assert.Contains(PluginManifest.FileName, reasons["e-no-manifest"], StringComparison.Ordinal);
        Assert.Contains("Told to fail", reasons["g-failing"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_plugins_folder_only_what_the_host_built_in_loads()
    {
        var host = Host();
        host.AddBuiltIn("naqel.builtin", registry => registry.AddSourceResolver(new FileSourceResolver(), ResolverRole.Protocol));

        var set = await host.LoadAsync(Token);

        Assert.Single(set.Plugins);
        Assert.Empty(set.Failures);
    }

    // Reads a file the plugin may be writing to meanwhile, or nothing while it can't be read
    private static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
