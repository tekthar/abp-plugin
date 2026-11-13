using System.Text.RegularExpressions;
using AbpPlugin;

namespace AbpPlugin.Hosting;

/// <summary>
/// <see cref="IPluginContributions"/> for one plugin: only the extension points it owns ("{key}.{name}@{n}", exact and
/// case-sensitive), never the host's or another plugin's. The items are copies, so holding them keeps no plugin loaded.
/// </summary>
public sealed partial class PluginContributionsFor(PluginRuntime runtime, string pluginKey) : IPluginContributions
{
    public long Generation => runtime.Generation;

    public IReadOnlyList<PluginContributionItem> Get(string extensionPoint)
    {
        if (!extensionPoint.StartsWith(pluginKey + ".", StringComparison.Ordinal) || !OwnPoint().IsMatch(extensionPoint[(pluginKey.Length + 1)..]))
        {
            throw new ArgumentException($"A plugin reads only its own extension points, named \"{pluginKey}.(name)@(version)\".", nameof(extensionPoint));
        }

        return runtime.GetContributions(extensionPoint).Select(c => new PluginContributionItem(c.PluginKey, c.PluginVersion, c.Item.Clone())).ToList();
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.-]*@[0-9]+$", RegexOptions.None, PluginHostPatterns.TimeoutMilliseconds)]
    private static partial Regex OwnPoint();
}
