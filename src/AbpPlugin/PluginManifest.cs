using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AbpPlugin;
using AbpPlugin.Packaging;

namespace AbpPlugin.Hosting;

/// <summary>A package's manifest (<see cref="PluginPackageFormat.Manifest"/>). The same format describes core modules, plugins and remote apps.</summary>
public sealed partial class PluginManifest
{
    /// <summary>The manifest's file name, in the application's format.</summary>
    public static string FileName => PluginPackageFormat.Current.Manifest;

    /// <summary>The name it had in packages made before, read when a package has no <see cref="FileName"/>; null when it had none.</summary>
    public static string? OldFileName => PluginPackageFormat.Current.FormerManifests.FirstOrDefault();

    /// <summary>The names a manifest is looked for under, its own first.</summary>
    public static IReadOnlyList<string> FileNames => [.. PluginPackageFormat.Current.Manifests];

    /// <summary>The manifest's path in a plugin's folder, or null when it has none.</summary>
    public static string? PathIn(string folder) => PluginPackageFormat.Current.ManifestIn(folder);

    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Kind { get; init; } = "plugin";
    [JsonPropertyName("system")]
    public bool IsSystem { get; init; }
    public PluginVendor? Vendor { get; init; }
    public Dictionary<string, string> Requires { get; init; } = new();
    public PluginServerSection? Server { get; init; }
    public PluginWebSection? Web { get; init; }

    /// <summary>
    /// The rest of the manifest by name, as JSON: contributions to extension points owned by modules, keyed by extension
    /// point id (<c>"projects.feature@1": [ … ]</c>), which the owning module reads and validates; and the sections the
    /// host application reads (<see cref="PluginHostProfile.ManifestSections"/>), read by <see cref="Section{T}"/>, such as
    /// an application's <c>ui</c>.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Contributions { get; init; } = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object?> _sections = new(StringComparer.Ordinal);

    /// <summary>
    /// A section of the manifest by its name (any case), read as <typeparamref name="T"/> once and kept; null when the
    /// manifest hasn't got it.
    /// </summary>
    public T? Section<T>(string name) where T : class =>
        (T?)_sections.GetOrAdd(name, n => Contributions.FirstOrDefault(c => string.Equals(c.Key, n, StringComparison.OrdinalIgnoreCase)) is { Key: not null } found
            && found.Value.ValueKind != JsonValueKind.Null
                ? found.Value.Deserialize<T>(JsonOptions)
                : null);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PluginManifest Load(string folder)
    {
        string path = PathIn(folder) ?? throw new PluginException($"No {FileName} found in '{folder}'.");
        PluginManifest manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), JsonOptions)
            ?? throw new PluginException($"{Path.GetFileName(path)} in '{folder}' is empty.");

        // An app ID; a key of one word, as plugins had before, is still read, to be told what to install.
        if (!AppIds.IsAppId(manifest.Key) && !KeyFormat().IsMatch(manifest.Key))
        {
            throw new PluginException(PluginKeys.NotAnAppId(manifest.Key));
        }

        _ = SemanticVersion.Parse(manifest.Version);
        // The host application's sections, read and checked now, so a package with a bad one is refused on install.
        foreach (IPluginManifestSection section in PluginHostProfile.Current.ManifestSections)
        {
            section.Check(manifest);
        }

        return manifest;
    }

    /// <summary>A plugin's key before app IDs.</summary>
    [GeneratedRegex("^[a-z][a-z0-9-]{1,48}$", RegexOptions.None, PluginHostPatterns.TimeoutMilliseconds)]
    private static partial Regex KeyFormat();
}

public sealed record PluginVendor(string Name, string? Url);

public sealed record PluginServerSection(string Assembly, string Entry);

public sealed record PluginWebSection(string RemoteName, string Entry);

/// <summary>An installed package made with its plugin's former key: the plugin is held until its version made with its app ID is installed.</summary>
public sealed class PluginBuiltBeforeException(string former, string appId) : PluginException(PluginKeys.BuiltBefore(former, appId));

/// <summary>
/// An installed plugin held, Failed, as it was loaded, until a version that fits is installed: made with its former key,
/// or using what this server hasn't got. Its row says why.
/// </summary>
public sealed class PluginHeldException(string message) : PluginException(message);
