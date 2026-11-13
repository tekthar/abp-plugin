using Microsoft.Extensions.Configuration;
using AbpPlugin;

namespace AbpPlugin.Hosting;

/// <summary>
/// Plugins are known by app IDs: <c>publisher.app</c>, as acme.reports. An application's own plugins may have had keys
/// of one word before; <see cref="Former"/> maps each to its app ID, and a former key is still read for one release (an
/// installed package, a URL, a configuration section, a tool's name, a module a plugin requires). Four plugins keep the
/// schemas they had before, for good (<see cref="SchemaOf"/>).
/// </summary>
public static class PluginKeys
{
    /// <summary>The application's plugins' keys before app IDs, and their app IDs (its profile's).</summary>
    public static IReadOnlyDictionary<string, string> Former => PluginHostProfile.Current.FormerKeys;

    /// <summary>The modules a plugin may require, by their former names, and their names now (the profile's).</summary>
    public static IReadOnlyDictionary<string, string> FormerModules => PluginHostProfile.Current.FormerModules;

    /// <summary>Schemas some plugins keep for good, by app ID (the profile's): their migrations and SQL name them.</summary>
    private static IReadOnlyDictionary<string, string> PinnedSchemas => PluginHostProfile.Current.PinnedSchemas;

    /// <summary>A plugin's key now: the app ID of a former key, else the key as it is.</summary>
    public static string Current(string key) => Former.GetValueOrDefault(key) ?? key;

    /// <summary>The key an app ID had before, else null.</summary>
    public static string? FormerOf(string appId) => Former.FirstOrDefault(p => p.Value == appId).Key;

    /// <summary>A module's or a plugin's name as a manifest's "requires" may give it, as it's known now.</summary>
    public static string CurrentRequirement(string name) => FormerModules.GetValueOrDefault(name) ?? Current(name);

    /// <summary>The schema a plugin's tables live in: a pinned one's, else <see cref="PluginSchemaNames.For"/>.</summary>
    public static string SchemaOf(string key) => PinnedSchemas.GetValueOrDefault(Current(key)) ?? AbpPlugin.PluginSchemaNames.For(key);

    /// <summary>
    /// Where a plugin's settings are: <c>Plugins:{publisher}:{app}</c> for an app ID, so the environment can set them
    /// (<c>Plugins__acme__reports__Enabled</c>: a shell variable can't hold a dot), else <c>Plugins:{key}</c>.
    /// </summary>
    public static string ConfigurationSection(string key) =>
        AppIds.IsAppId(key) ? $"Plugins:{AppIds.PublisherOf(key)}:{AppIds.AppOf(key)}" : $"Plugins:{key}";

    /// <summary>
    /// A plugin's settings: its own section, over its former key's (<c>Plugins:seeder</c>), read for one release.
    /// </summary>
    public static IConfiguration ConfigurationOf(IConfiguration configuration, string key)
    {
        IConfigurationSection section = configuration.GetSection(ConfigurationSection(key));
        if (FormerOf(key) is not { } former)
        {
            return section;
        }

        return new ConfigurationBuilder()
            .AddConfiguration(configuration.GetSection($"Plugins:{former}"))
            .AddConfiguration(section)
            .Build();
    }

    /// <summary>Why a package made with a former key isn't installed, upgraded or loaded (kb/plugins/built-before-app-ids.md).</summary>
    public static string BuiltBefore(string former, string appId) =>
        $"'{former}' is now {appId}: install its version made with that key. It was made before plugins were known by app IDs.";

    /// <summary>Why a key that isn't an app ID can't be a new plugin's.</summary>
    public static string NotAnAppId(string key) =>
        $"Plugin key '{key}' isn't an app ID: the publisher's slug, a dot and the app's slug, as acme.ratings.";

    /// <summary>The former keys' configuration sections still set ("Plugins:seeder"), for the warning at start.</summary>
    public static IReadOnlyList<string> FormerSectionsStillSet(IConfiguration configuration) =>
        [.. Former.Keys.Select(former => $"Plugins:{former}").Where(path => configuration.GetSection(path).Exists())];
}
