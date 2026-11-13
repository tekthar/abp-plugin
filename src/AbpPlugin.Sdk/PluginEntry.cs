namespace AbpPlugin;

/// <summary>
/// The entry class of a plugin, named in its manifest under server.entry.
/// Everything registered in <see cref="ConfigureServices"/> is tracked by the loader,
/// so disabling the plugin removes all of it.
/// </summary>
public abstract class PluginEntry
{
    public abstract void ConfigureServices(IPluginServices services);
}

/// <summary>Facts about the running plugin, available to plugin code through DI.</summary>
public sealed record PluginContext(
    string Key,
    string Version,
    string Schema,
    string ConnectionString,
    string ContentRoot);
