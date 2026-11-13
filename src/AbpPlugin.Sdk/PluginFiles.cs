using System.Text.RegularExpressions;

namespace AbpPlugin;

/// <summary>
/// A plugin's own files in the host's storage: kept under <c>plugin-files/{plugin key}/</c> in the
/// file system or the bucket in use, so they outlive the server that saved them. Each plugin sees only its own. They're
/// inputs a plugin was given (an import's backup, say), not records: "Copy from the previous storage" leaves them.
/// </summary>
public interface IPluginFiles
{
    /// <summary>Saves (or replaces) a file. The content is read to its end.</summary>
    Task SaveAsync(string name, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// The file, readable from anywhere in it (a zip needs that), or null when there's none by that name. A provider
    /// whose stream can't seek is copied to a local temporary file first, which goes when the stream is closed.
    /// </summary>
    Task<Stream?> OpenReadAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>What a plugin file may be called.</summary>
public static partial class PluginFileNames
{
    public const int MaxLength = 200;

    /// <summary>Letters, digits, dot, dash and underscore, not starting with a dot: never a path.</summary>
    public static bool IsValid(string? name) => name is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9_\-][A-Za-z0-9._\-]*$", RegexOptions.None, PluginPatterns.TimeoutMilliseconds)]
    private static partial Regex Pattern();
}
