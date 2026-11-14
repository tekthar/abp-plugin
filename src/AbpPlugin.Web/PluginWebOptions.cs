namespace AbpPlugin.Web;

/// <summary>Where and how the plugins' web parts are served.</summary>
public sealed class PluginWebOptions
{
    /// <summary>The address the plugins' web parts are served under: <c>{Prefix}/{key}/{folder}/{file}</c>.</summary>
    public string Prefix { get; set; } = "/plugins-web";

    /// <summary>
    /// The plugins' folder their web part is in, inside the folder a version is installed in (the package's
    /// <c>web/</c>).
    /// </summary>
    public string Part { get; set; } = "web";

    /// <summary>
    /// The <c>Cache-Control</c> a plugin's file is sent with. A browser asks again each time by default, so a plugin
    /// disabled or uninstalled stops being served at once.
    /// </summary>
    public string CacheControl { get; set; } = "no-cache";
}
