using System.Text.Json;

namespace AbpPlugin.Packaging;

/// <summary>
/// What a package's manifest says of itself: its key (the app ID), its version and the versions of what it runs on
/// (<c>requires</c>, as <c>{"host": ">=0.1.0 &lt;2.0.0"}</c>). The host reads the whole manifest; this is
/// the part a marketplace or a build checks before a package reaches one.
/// </summary>
public sealed record PluginPackageManifest(string Key, SemanticVersion Version, IReadOnlyDictionary<string, string> Requires)
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Reads a manifest's text; a manifest without a key or a version is refused, saying which.</summary>
    public static PluginPackageManifest Parse(string json)
    {
        using JsonDocument document = ParseDocument(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new PluginException("The manifest must be a JSON object.");
        }

        string key = Text(root, "key") ?? throw new PluginException("The manifest has no key.");
        string versionText = Text(root, "version") ?? throw new PluginException("The manifest has no version.");
        SemanticVersion version;
        try
        {
            version = SemanticVersion.Parse(versionText);
        }
        catch (PluginException)
        {
            throw new PluginException($"The manifest's version, {versionText}, isn't one to three numbers.");
        }

        Dictionary<string, string> requires = new(StringComparer.Ordinal);
        if (root.TryGetProperty("requires", out JsonElement needs) && needs.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty need in needs.EnumerateObject())
            {
                if (need.Value.ValueKind == JsonValueKind.String)
                {
                    requires[need.Name] = need.Value.GetString()!;
                }
            }
        }

        return new PluginPackageManifest(key, version, requires);
    }

    /// <summary>Reads the manifest in a package's folder, or null when the folder has none.</summary>
    public static PluginPackageManifest? Read(string folder) =>
        PluginPackageFormat.Current.ManifestIn(folder) is { } path ? Parse(File.ReadAllText(path)) : null;

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            throw new PluginException($"The manifest isn't valid JSON: {ex.Message}");
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
