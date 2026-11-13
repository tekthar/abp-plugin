using System.Text.RegularExpressions;

namespace AbpPlugin;

/// <summary>
/// App IDs: what a plugin is known by, for good. The publisher's slug, a dot and the app's own slug
/// (acme.ratings), so only a publisher uses its prefix and two publishers' plugins never clash. A plugin manifest's key
/// is its app ID, and the marketplace addresses its listing by it.
/// </summary>
public static partial class AppIds
{
    /// <summary>The longest a slug can be.</summary>
    public const int MaxSlugLength = 64;

    /// <summary>The longest an app ID can be: two slugs and the dot.</summary>
    public const int MaxLength = 2 * MaxSlugLength + 1;

    /// <summary>
    /// A publisher's or an app's slug: 1 to 64 lowercase letters, digits and single hyphens, starting and ending with a
    /// letter or digit. So a slug holds no "_", no "." and no "--".
    /// </summary>
    public static bool IsSlug(string? text) => text is not null && SlugPattern().IsMatch(text) && !text.Contains("--", StringComparison.Ordinal);

    public static bool IsAppId(string? text)
    {
        int dot = text?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        return dot > 0 && IsSlug(text![..dot]) && IsSlug(text[(dot + 1)..]);
    }

    /// <summary>The publisher's part of an app ID: acme for acme.ratings.</summary>
    public static string PublisherOf(string appId) => Check(appId)[..appId.IndexOf('.', StringComparison.Ordinal)];

    /// <summary>The app's own part of an app ID: ratings for acme.ratings.</summary>
    public static string AppOf(string appId) => Check(appId)[(appId.IndexOf('.', StringComparison.Ordinal) + 1)..];

    public static string Check(string? text) =>
        IsAppId(text) ? text! : throw new ArgumentException($"'{text}' isn't an app ID: the publisher's slug, a dot and the app's slug, as acme.ratings.", nameof(text));

    // \z, not $: $ also matches before a last "\n".
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\z", RegexOptions.None, PluginPatterns.TimeoutMilliseconds)]
    private static partial Regex SlugPattern();
}
