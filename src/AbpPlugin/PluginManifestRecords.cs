using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using AbpPlugin.Packaging;

namespace AbpPlugin.Hosting;

/// <summary>
/// A plugin's manifest and messages as installed, so what it declares can be read while it's
/// disabled, and after it's uninstalled while its data stays. One row a plugin, the latest version: <see cref="PluginManager"/>
/// writes it with the plugin's row and marks it removed when the plugin is uninstalled. The platform keeps the manifest
/// whole and reads nothing of it here; whoever owns an extension point reads its part.
/// </summary>
public class PluginManifestRecord
{
    protected PluginManifestRecord()
    {
    }

    public PluginManifestRecord(string key) => Key = key;

    public string Key { get; private set; } = null!;
    public string Name { get; set; } = null!;
    public string Version { get; set; } = null!;

    /// <summary>The manifest as installed.</summary>
    public string Manifest { get; set; } = null!;

    /// <summary>Its <c>messages/*.json</c> by language, flattened as the site's texts are: <c>{"ar": {"a.b": "…"}}</c>.</summary>
    public string Messages { get; set; } = "{}";

    public DateTime InstalledAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>When it was uninstalled; null while it's installed, enabled or not.</summary>
    public DateTime? RemovedAt { get; set; }

    /// <summary>Takes what a plugin's folder or package holds now, as installed again.</summary>
    public void Take(PluginManifestFiles files)
    {
        (Name, Version, Manifest, Messages) = (files.Name, files.Version, files.Manifest, files.Messages);
        UpdatedAt = DateTime.UtcNow;
        if (InstalledAt == default || RemovedAt is not null)
        {
            InstalledAt = UpdatedAt;
        }

        RemovedAt = null;
    }
}

/// <summary>A plugin's manifest and messages, read from its folder or its package without unpacking the rest.</summary>
public sealed record PluginManifestFiles(string Key, string Name, string Version, string Manifest, string Messages)
{
    private const string MessagesFolder = "messages";

    public static PluginManifestFiles FromFolder(string folder)
    {
        string manifest = File.ReadAllText(PluginManifest.PathIn(folder) ?? Path.Combine(folder, PluginManifest.FileName));
        string messages = Path.Combine(folder, MessagesFolder);
        IEnumerable<(string, string)> files = Directory.Exists(messages)
            ? Directory.EnumerateFiles(messages, "*.json").Select(f => (Path.GetFileNameWithoutExtension(f), File.ReadAllText(f)))
            : [];
        return Of(manifest, files);
    }

    /// <summary>From a package's zip: its manifest (or a former name's) and <c>messages/*.json</c>, nothing else read.</summary>
    public static PluginManifestFiles FromZip(Stream zip)
    {
        using ZipArchive archive = new(zip, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry entry = PluginManifest.FileNames.Select(archive.GetEntry).FirstOrDefault(e => e is not null)
            ?? throw new PluginException($"The package has no {PluginManifest.FileName}.");
        List<(string, string)> files = [];
        foreach (ZipArchiveEntry message in archive.Entries.Where(e =>
            e.FullName.Replace('\\', '/') is var name && name.StartsWith(MessagesFolder + "/", StringComparison.Ordinal)
            && name.Count(c => c == '/') == 1 && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            files.Add((Path.GetFileNameWithoutExtension(message.Name), Read(message)));
        }

        return Of(Read(entry), files);
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using StreamReader reader = new(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>Nested message JSON to a flat map of dotted keys, as the application's texts read it; non-string leaves are skipped.</summary>
    private static Dictionary<string, string> Flatten(JsonElement root)
    {
        Dictionary<string, string> flat = new(StringComparer.Ordinal);
        Walk(root, null, flat);
        return flat;

        static void Walk(JsonElement element, string? prefix, Dictionary<string, string> flat)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Walk(property.Value, prefix is null ? property.Name : $"{prefix}.{property.Name}", flat);
                }
            }
            else if (element.ValueKind == JsonValueKind.String && prefix is not null)
            {
                flat[prefix] = element.GetString()!;
            }
        }
    }

    private static PluginManifestFiles Of(string manifest, IEnumerable<(string Language, string Json)> messages)
    {
        using JsonDocument parsed = JsonDocument.Parse(manifest, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        JsonElement root = parsed.RootElement;
        JsonObject texts = [];
        foreach ((string language, string json) in messages.OrderBy(m => m.Language, StringComparer.Ordinal))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                texts[language] = new JsonObject(Flatten(doc.RootElement).Select(t => KeyValuePair.Create(t.Key, (JsonNode?)t.Value)));
            }
            catch (JsonException)
            {
                // A messages file that isn't JSON is skipped here as the site's texts skip it.
            }
        }

        return new PluginManifestFiles(root.GetProperty("key").GetString()!, root.GetProperty("name").GetString()!, root.GetProperty("version").GetString()!,
            manifest, texts.ToJsonString());
    }
}
