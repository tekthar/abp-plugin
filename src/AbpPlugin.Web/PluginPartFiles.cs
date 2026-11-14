using AbpPlugin.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AbpPlugin.Web;

/// <summary>
/// The files of a part of the enabled plugins (their web part, or another an application gives them, such as a phone
/// app's) as a browser or an app asks for them: <c>{prefix}/{key}/{folder}/{file}</c> is
/// <c>{PluginsRoot}/{key}/{folder}/{part}/{file}</c>, the folder being the one a version is installed in.
/// </summary>
public static class PluginPartFiles
{
    /// <summary>Maps <c>GET {prefix}/{key}/{folder}/{**file}</c> to the files of the plugins' <paramref name="part"/>.</summary>
    public static IEndpointConventionBuilder MapPluginPartFiles(this IEndpointRouteBuilder app, string prefix, string part) =>
        app.MapGet($"{prefix.TrimEnd('/')}/{{key}}/{{folder}}/{{**file}}", (HttpContext http, string key, string folder, string file) =>
            Serve(http, key, folder, part, file));

    /// <summary>
    /// An enabled plugin's file only: a disabled or uninstalled one's stay on disk but aren't served. Any of its installed
    /// versions' is, so a page opened before an upgrade still loads the chunks it asks for later; and by a former key
    /// (<see cref="PluginKeys"/>). Never a file outside the part's folder.
    /// </summary>
    public static IResult Serve(HttpContext http, string key, string folder, string part, string file)
    {
        PluginRuntime runtime = http.RequestServices.GetRequiredService<PluginRuntime>();
        if (!runtime.IsEnabled(key))
        {
            key = PluginKeys.Current(key);
            if (!runtime.IsEnabled(key))
            {
                return Results.NotFound();
            }
        }

        string pluginsRoot = http.RequestServices.GetRequiredService<IOptions<PluginHostOptions>>().Value.PluginsRoot;
        string? full = Resolve(Path.Combine(pluginsRoot, key, folder, part), file);
        if (full is null || !File.Exists(full))
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = http.RequestServices.GetService<IOptions<PluginWebOptions>>()?.Value.CacheControl ?? "no-cache";
        return Results.File(full, ContentTypeOf(full));
    }

    /// <summary>The file's full path inside <paramref name="root"/>, or null when the path leads out of it.</summary>
    public static string? Resolve(string root, string file)
    {
        string inside = Path.GetFullPath(root);
        string full = Path.GetFullPath(Path.Combine(inside, file));
        return full.StartsWith(inside + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>The content type a file is served with, by its extension.</summary>
    public static string ContentTypeOf(string path) => Path.GetExtension(path) switch
    {
        // Re.Pack names a phone app's scripts *.bundle.
        ".js" or ".mjs" or ".bundle" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".map" => "application/json",
        _ => "application/octet-stream",
    };
}
