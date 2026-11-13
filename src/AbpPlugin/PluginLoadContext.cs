using System.Reflection;
using System.Runtime.Loader;

namespace AbpPlugin.Hosting;

/// <summary>
/// One collectible load context per plugin version, the .NET equivalent of an OSGi bundle's ClassLoader.
/// Any assembly the host already has (the application's contracts, ABP, ASP.NET, EF Core, Npgsql) resolves from
/// the host so types match on both sides; everything else loads privately from the plugin folder.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly Lazy<HashSet<string>> HostAssemblyNames = new(ReadHostAssemblyNames);

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string name, string mainAssemblyPath)
        : base(name, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    /// <summary>Whether an assembly of this name is the host's, so a plugin shares it rather than loading its own.</summary>
    internal static bool IsHostAssembly(string name) => HostAssemblyNames.Value.Contains(name);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && HostAssemblyNames.Value.Contains(assemblyName.Name))
        {
            return null; // shared with the host
        }

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private static HashSet<string> ReadHostAssemblyNames()
    {
        string tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            names.Add(Path.GetFileNameWithoutExtension(path));
        }

        foreach (Assembly loaded in Default.Assemblies)
        {
            if (loaded.GetName().Name is { } loadedName)
            {
                names.Add(loadedName);
            }
        }

        return names;
    }
}
