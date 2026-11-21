using System.Reflection;
using System.Runtime.Loader;

namespace Naqel.Manager;

/// <summary>
/// The load context of one plugin: its own assemblies come from its folder, so two plugins can bring different
/// versions of a library, while the contracts they share with Naqel always come from Naqel itself.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    // The assemblies whose types cross between a plugin and Naqel; a plugin's own copy of them would make its
    // resolvers and engines types Naqel doesn't know
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase) { "Naqel.Core", "Naqel.Plugins" };

    private readonly string _directory;
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string name, string mainAssembly) : base(name)
    {
        _directory = Path.GetDirectoryName(mainAssembly)!;
        _resolver = new AssemblyDependencyResolver(mainAssembly);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == null || Shared.Contains(assemblyName.Name))
        {
            return null;
        }
        // The plugin's .deps.json if it has one, otherwise a file of that name next to it
        var path = _resolver.ResolveAssemblyToPath(assemblyName) ?? Path.Combine(_directory, assemblyName.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
