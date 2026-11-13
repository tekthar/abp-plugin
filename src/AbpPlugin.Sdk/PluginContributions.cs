using System.Text.Json;

namespace AbpPlugin;

/// <summary>A contribution an enabled plugin's manifest makes to an extension point: one array item, as written.</summary>
public sealed record PluginContributionItem(string PluginKey, string PluginVersion, JsonElement Item);

/// <summary>
/// The manifest contributions to the extension points this plugin owns: names made of its own key, a dot, a name and a
/// version ("acme.reports.chart@1" for the plugin "acme.reports"). Asking about any other point throws, the host's own
/// included. Items are other plugins' JSON: validate each against your own schema and skip the bad ones.
/// </summary>
public interface IPluginContributions
{
    /// <summary>Changes whenever a plugin is enabled or disabled: cache what you build from the items per generation.</summary>
    long Generation { get; }

    /// <summary>Enabled plugins' items for the point, in plugin key order.</summary>
    IReadOnlyList<PluginContributionItem> Get(string extensionPoint);
}
