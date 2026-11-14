using AbpPlugin.Web;

namespace AbpPlugin.SampleHost;

/// <summary>
/// What the sample adds to each plugin in the remotes list: the plugin's name, as an application adds its own sections
/// (the pages a plugin adds to it, its menu entries) and leaves out a plugin the person may not see.
/// </summary>
public sealed class SampleWebSections : IPluginWebSections
{
    public ValueTask<IReadOnlyDictionary<string, object?>?> ForAsync(PluginWebRemote remote, HttpContext http) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, object?>?>(new Dictionary<string, object?> { ["name"] = remote.Plugin.Manifest.Name });
}
