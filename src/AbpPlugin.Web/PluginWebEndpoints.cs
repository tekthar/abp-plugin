using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AbpPlugin.Web;

/// <summary>
/// What an application adds to each plugin in the remotes list, for the person asking: its own sections from the
/// plugin's manifest (the pages it adds, its menu entries) by name, or null to leave the plugin out of the list for them.
/// The application registers its own; by default each plugin is listed with its remote alone.
/// </summary>
public interface IPluginWebSections
{
    ValueTask<IReadOnlyDictionary<string, object?>?> ForAsync(PluginWebRemote remote, HttpContext http);
}

internal sealed class NoPluginWebSections : IPluginWebSections
{
    private static readonly IReadOnlyDictionary<string, object?> None = new Dictionary<string, object?>();

    public ValueTask<IReadOnlyDictionary<string, object?>?> ForAsync(PluginWebRemote remote, HttpContext http) => ValueTask.FromResult<IReadOnlyDictionary<string, object?>?>(None);
}

/// <summary>The plugins' web parts on an application's routes.</summary>
public static class PluginWebEndpoints
{
    /// <summary>Names each plugin's entry in the remotes list has; an application's sections use others.</summary>
    public static readonly IReadOnlySet<string> OwnNames = new HashSet<string>(StringComparer.Ordinal) { "key", "version", "remote" };

    /// <summary>
    /// The enabled plugins' web files at <see cref="PluginWebOptions.Prefix"/>, and their remotes at
    /// <paramref name="remotesPath"/>. Returns the remotes endpoint, which an application usually requires a sign-in
    /// for (<c>.RequireAuthorization()</c>): the files are a plugin's code, public as any page's scripts are.
    /// </summary>
    public static RouteHandlerBuilder MapPluginWeb(this IEndpointRouteBuilder app, string remotesPath = "/api/plugins-web/remotes")
    {
        PluginWebOptions options = app.ServiceProvider.GetRequiredService<IOptions<PluginWebOptions>>().Value;
        app.MapPluginPartFiles(options.Prefix, options.Part);
        return app.MapPluginWebRemotes(remotesPath);
    }

    /// <summary>
    /// <c>GET {path}</c>: <c>{ "plugins": [{ "key", "version", "remote": { "name", "entry" }, ...the application's
    /// sections }] }</c>, each enabled plugin with a web part, by key, that the application's sections leave in.
    /// </summary>
    public static RouteHandlerBuilder MapPluginWebRemotes(this IEndpointRouteBuilder app, string path = "/api/plugins-web/remotes") =>
        app.MapGet(path, async (HttpContext http, PluginWebRemotes remotes, IPluginWebSections sections) =>
        {
            List<Dictionary<string, object?>> plugins = [];
            foreach (PluginWebRemote remote in remotes.Enabled())
            {
                IReadOnlyDictionary<string, object?>? extra = await sections.ForAsync(remote, http);
                if (extra is null)
                {
                    continue;
                }

                Dictionary<string, object?> entry = new(StringComparer.Ordinal)
                {
                    ["key"] = remote.Key,
                    ["version"] = remote.Version,
                    ["remote"] = new Dictionary<string, string> { ["name"] = remote.Name, ["entry"] = remote.Entry },
                };
                foreach ((string name, object? value) in extra)
                {
                    if (!entry.TryAdd(name, value))
                    {
                        throw new InvalidOperationException($"A plugin's section can't be named '{name}': each plugin's entry has it already.");
                    }
                }

                plugins.Add(entry);
            }

            return Results.Json(new Dictionary<string, object?> { ["plugins"] = plugins });
        });
}
