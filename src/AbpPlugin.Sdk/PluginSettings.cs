using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace AbpPlugin;

/// <summary>
/// A plugin's own settings by key: JSON values under names of its choosing, kept by the host, read and
/// written here from the plugin's code and through the host's settings API from its web part. Each
/// plugin sees only its own. A write joins the call's unit of work, so it commits with the plugin's tables
/// and the server's changes, or not at all. Settings are configuration: data goes in tables of the plugin's own.
/// </summary>
public interface IPluginSettings
{
    /// <summary>The setting's value, or null when there's none by that key.</summary>
    Task<JsonElement?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Sets (or replaces) a setting. Any JSON value but null, up to <see cref="PluginSettingKeys.MaxValueBytes"/>.</summary>
    Task SetAsync(string key, JsonElement value, CancellationToken cancellationToken = default);

    /// <summary>Removes a setting; false when there was none.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>The plugin's settings whose keys start with <paramref name="prefix"/> (all of them without one), by key.</summary>
    Task<IReadOnlyList<PluginSetting>> ListAsync(string? prefix = null, CancellationToken cancellationToken = default);
}

/// <summary>A setting as it's kept: who set it last is the signed-in person whose call it was, or none (a job, the worker).</summary>
public sealed record PluginSetting(string Key, JsonElement Value, DateTime UpdatedAt, Guid? UpdatedBy);

/// <summary>What a setting may be called and hold.</summary>
public static partial class PluginSettingKeys
{
    public const int MaxLength = 200;

    /// <summary>A value's most, as UTF-8 JSON.</summary>
    public const int MaxValueBytes = 64 * 1024;

    /// <summary>The most keys one plugin keeps.</summary>
    public const int MaxPerPlugin = 1000;

    /// <summary>Keys under this prefix may be read by anyone signed in, through the host's API; the rest only by site admins.</summary>
    public const string SharedPrefix = "shared.";

    /// <summary>Letters, digits, dot, dash and underscore, starting with a letter or a digit.</summary>
    public static bool IsValid(string? key) => key is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(key);

    /// <summary>Whether anyone signed in may read it (<see cref="SharedPrefix"/>).</summary>
    public static bool IsShared(string key) => key.StartsWith(SharedPrefix, StringComparison.Ordinal);

    /// <summary>Why the key can't be one, or null when it can.</summary>
    public static string? Problem(string? key) =>
        IsValid(key) ? null : $"\"{key}\" can't be a setting's key: use up to {MaxLength} letters, digits, '.', '-' and '_', starting with a letter or a digit.";

    /// <summary>Why the value can't be kept, or null when it can: null isn't a value (delete the setting), and it must fit.</summary>
    public static string? ValueProblem(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "A setting can't be null: delete it instead.";
        }

        int bytes = Encoding.UTF8.GetByteCount(value.GetRawText());
        return bytes > MaxValueBytes ? $"A setting's value can be at most {MaxValueBytes / 1024} KiB; this one is {(bytes + 1023) / 1024} KiB." : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$", RegexOptions.None, PluginPatterns.TimeoutMilliseconds)]
    private static partial Regex Pattern();
}

/// <summary>Typed settings: values written and read as your own types.</summary>
public static class PluginSettingsExtensions
{
    /// <summary>
    /// The setting as <typeparamref name="T"/>, or default when there's none. Without <paramref name="options"/> it reads
    /// with web defaults (camelCase) through a serializer made for the call, so the host never caches your types and
    /// your plugin can unload; pass options of your own when you read often.
    /// </summary>
    public static async Task<T?> GetAsync<T>(this IPluginSettings settings, string key, JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        JsonElement? value = await settings.GetAsync(key, cancellationToken);
        return value is { } v ? v.Deserialize<T>(options ?? CallOptions()) : default;
    }

    /// <summary>Sets the setting to <paramref name="value"/>, written as <see cref="GetAsync{T}"/> reads it.</summary>
    public static Task SetAsync<T>(this IPluginSettings settings, string key, T value, JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default) =>
        settings.SetAsync(key, JsonSerializer.SerializeToElement(value, options ?? CallOptions()), cancellationToken);

    // A resolver of its own: System.Text.Json shares caching contexts between options with equal settings, which would let
    // the host's long-lived options keep an unloaded plugin's types (as IPluginJson's).
    private static JsonSerializerOptions CallOptions() => new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
}
