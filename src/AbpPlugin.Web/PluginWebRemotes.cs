using AbpPlugin.Hosting;
using Microsoft.Extensions.Options;

namespace AbpPlugin.Web;

/// <summary>An enabled plugin's web part as a browser loads it: its remote's name, and its entry's address.</summary>
public sealed record PluginWebRemote(LoadedPlugin Plugin, string Name, string Entry)
{
    public string Key => Plugin.Key;

    public string Version => Plugin.Manifest.Version;
}

/// <summary>
/// The enabled plugins' Module Federation remotes, from their manifests' <c>web</c> section (its remote's name, and
/// its entry inside the package's <c>web/</c>). An application's own list of what plugins add to its pages starts
/// here, or the remotes endpoint gives it (<see cref="PluginWebEndpoints"/>).
/// </summary>
public sealed class PluginWebRemotes(PluginRuntime runtime, IOptions<PluginWebOptions> options)
{
    /// <summary>Every enabled plugin that has a web part, by key, with its remote.</summary>
    public IEnumerable<PluginWebRemote> Enabled()
    {
        PluginWebOptions web = options.Value;
        string folder = web.Part.Trim('/') + "/";
        return runtime.Enabled
            .Where(p => p.Manifest.Web is not null)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new PluginWebRemote(p, p.Manifest.Web!.RemoteName, Address(web.Prefix, p, Inside(p.Manifest.Web.Entry, folder))));
    }

    /// <summary>
    /// A file of a plugin's part at <c>{prefix}/{key}/{folder}/{file}</c>. The folder is the one its version is installed
    /// in: the version, or the version and a suffix when that version was installed before and its folder is still kept.
    /// </summary>
    public static string Address(string prefix, LoadedPlugin plugin, string file) =>
        $"{prefix.TrimEnd('/')}/{plugin.Key}/{Path.GetFileName(plugin.Folder)}/{file}";

    /// <summary>The entry as the manifest gives it (<c>web/remoteEntry.js</c>), from inside the part's folder.</summary>
    private static string Inside(string entry, string folder) => entry.Replace(folder, string.Empty);
}
