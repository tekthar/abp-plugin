namespace AbpPlugin.Hosting;

/// <summary>The plugin host's own options, which the application configures.</summary>
public sealed class PluginHostOptions
{
    /// <summary>Where installed plugin versions are kept: {PluginsRoot}/{key}/{version}/.</summary>
    public string PluginsRoot { get; set; } = Path.Combine(AppContext.BaseDirectory, "App_Data", "plugins");

    /// <summary>Folders holding the system plugins that ship with the application, installed or upgraded at startup.</summary>
    public List<string> SystemPluginFolders { get; } = [];

    /// <summary>The application's modules and their versions, checked against plugin manifests' "requires".</summary>
    public Dictionary<string, string> ModuleVersions { get; } = new(StringComparer.Ordinal);

    /// <summary>Safe mode: only the system plugins load, so a site a plugin breaks still starts and can disable it.</summary>
    public bool SafeMode { get; set; }
}
