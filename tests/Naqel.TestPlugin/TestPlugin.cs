using System.Runtime.CompilerServices;
using System.Text;
using Naqel.Plugins;
using Naqel.Streams;

namespace Naqel.TestPlugin;

/// <summary>
/// Registers a site resolver for <c>greeting:name</c> links, whose content is the <c>greeting</c> setting, and an
/// observer that writes the events it sees into the plugin's data folder. With the setting <c>fail</c> set to
/// <c>true</c>, it fails while registering.
/// </summary>
public sealed class TestPlugin : INaqelPlugin
{
    public void Register(IPluginRegistry registry, IPluginContext context)
    {
        registry.AddSettings(
        [
            new SettingDefinition("greeting", "Greeting", SettingKind.Text) { Default = "hello" },
            new SettingDefinition("fail", "Fail while registering", SettingKind.Boolean) { Default = "false" }
        ]);
        if (context.Settings.GetValue("fail") == "true")
        {
            throw new InvalidOperationException("Told to fail");
        }
        registry.AddSourceResolver(new GreetingResolver(context.Settings), ResolverRole.Site);
        registry.AddObserver(new FileObserver(Path.Combine(context.DataDirectory, "events.txt")));
    }
}

/// <summary>
/// Resolves <c>greeting:name</c> to a file called <c>name.txt</c> holding the current greeting.
/// </summary>
public sealed class GreetingResolver(IPluginSettings settings) : ISourceResolver
{
    public bool CanResolve(Uri reference) => reference.Scheme == "greeting";

    public async IAsyncEnumerable<Resolved> ResolveAsync(Uri reference, ResolveContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var content = Encoding.UTF8.GetBytes(settings.GetValue("greeting") ?? "");
        var name = reference.AbsolutePath + ".txt";
        yield return new ResolvedSource(StreamSource.From(new MemoryStream(content), name, content.Length), "", new SourceInfo { Name = name, Length = content.Length });
    }
}

/// <summary>
/// Writes each event's kind on a line of its own.
/// </summary>
public sealed class FileObserver(string path) : IJobObserver
{
    public Task OnEventAsync(JobEvent jobEvent, CancellationToken cancellationToken) =>
        File.AppendAllTextAsync(path, jobEvent.Kind + Environment.NewLine, cancellationToken);
}
