using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AbpPlugin.Hosting;

/// <summary>
/// A plugin's public endpoints: anyone may call them, under /api/plugins/{key}/public/…, so the host holds
/// them to a body of at most <see cref="MaxBody"/> and to so many calls a minute from one address, and to the plugin;
/// counted for the whole site (<see cref="IPluginCallLimits"/>), so a second server doesn't double them. The plugin checks what
/// a call says.
/// </summary>
public static class PublicCalls
{
    public const string Prefix = "public";

    /// <summary>A provider's callback or webhook is small; a megabyte is plenty.</summary>
    public const long MaxBody = 1024 * 1024;

    public const int PerAddress = 120;
    public const int PerPlugin = 3000;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Whether a path (or a pattern) is under <c>public/</c>.</summary>
    public static bool IsPublic(string? path)
    {
        string p = (path ?? string.Empty).TrimStart('/');
        return p.Equals(Prefix, StringComparison.OrdinalIgnoreCase) || p.StartsWith(Prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path under <c>public/</c>, as a public endpoint's pattern matches it.</summary>
    public static string Inner(string path) => path.TrimStart('/')[Prefix.Length..].TrimStart('/');

    /// <summary>Whether a request is for a plugin's public endpoint: <c>/api/plugins/{key}/public/…</c>.</summary>
    public static bool IsPublicRequest(PathString path) =>
        path.StartsWithSegments("/api/plugins", StringComparison.OrdinalIgnoreCase, out PathString rest)
        && (rest.Value ?? string.Empty).Split('/', 3, StringSplitOptions.RemoveEmptyEntries) is [_, var under, ..]
        && IsPublic(under);

    /// <summary>
    /// Before sign-in, which reads a form body for an access token: a public call's body is capped and kept readable
    /// again, so the plugin gets it exactly as it was sent (a signature is over those bytes).
    /// </summary>
    public static void Prepare(HttpContext http)
    {
        if (!IsPublicRequest(http.Request.Path))
        {
            return;
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
        {
            body.MaxRequestBodySize = MaxBody;
        }

        http.Request.EnableBuffering(64 * 1024, MaxBody);
    }

    /// <summary>The body again from its start, for the plugin, after whatever read it before (<see cref="Prepare"/>).</summary>
    public static void Rewind(HttpContext http)
    {
        if (http.Request.Body.CanSeek)
        {
            http.Request.Body.Position = 0;
        }
    }

    /// <summary>
    /// Whether the call may go on: 0, or the status to answer with: 413 for a body over <see cref="MaxBody"/>, 429 when
    /// the address or the plugin has had its calls this minute (with Retry-After). Caps the body it will read, too.
    /// </summary>
    public static async Task<int> CheckAsync(HttpContext http, string pluginKey, IPluginCallLimits limits, CancellationToken cancellationToken)
    {
        if (http.Request.ContentLength > MaxBody)
        {
            return StatusCodes.Status413PayloadTooLarge;
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
        {
            body.MaxRequestBodySize = MaxBody;
        }

        string address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        // Both counted before either refuses, so a busy address can't spend the plugin's calls without being counted.
        bool fromAddress = await limits.TryTakeAsync($"plugin-public|{pluginKey}|ip:{address}", PerAddress, Window, cancellationToken);
        bool forPlugin = await limits.TryTakeAsync($"plugin-public|{pluginKey}", PerPlugin, Window, cancellationToken);
        if (fromAddress && forPlugin)
        {
            return 0;
        }

        http.Response.Headers.RetryAfter = ((int)Window.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return StatusCodes.Status429TooManyRequests;
    }
}
