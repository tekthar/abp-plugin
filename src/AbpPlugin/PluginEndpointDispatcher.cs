using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.AspNetCore.ExceptionHandling;
using Volo.Abp.Http;
using Volo.Abp.Uow;

namespace AbpPlugin.Hosting;

/// <summary>
/// Serves /api/plugins/{key}/{**path}. Plugin endpoints are matched here instead of being added to
/// ASP.NET Core's routing tables, so nothing in MVC's caches ever references a plugin type. A signed-in caller's,
/// except under <c>public/</c>: those take anyone's call, within <see cref="PublicCalls"/>' limits. A former
/// key (<see cref="PluginHostProfile.FormerKeys"/>) is served as its app ID for one release, not redirected: a webhook's sender
/// doesn't follow redirects.
/// </summary>
public static class PluginEndpointDispatcher
{
    public static async Task HandleAsync(HttpContext http, string key, string? path)
    {
        PluginRuntime runtime = http.RequestServices.GetRequiredService<PluginRuntime>();
        LoadedPlugin? plugin = runtime.Find(key) ?? runtime.Find(PluginKeys.Current(key));
        if (plugin is null)
        {
            await Results.Problem($"Plugin '{key}' is not enabled.", statusCode: StatusCodes.Status404NotFound).ExecuteAsync(http);
            return;
        }

        bool isPublic = PublicCalls.IsPublic(path);
        if (!isPublic && http.User.Identity?.IsAuthenticated != true)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        string requestPath = isPublic ? PublicCalls.Inner(path!) : path ?? string.Empty;
        if (isPublic && plugin.Registrations.Endpoints.Exists(e => e.Public)
            && await PublicCalls.CheckAsync(http, plugin.Key, http.RequestServices.GetRequiredService<IPluginCallLimits>(), http.RequestAborted) is > 0 and var refused)
        {
            http.Response.StatusCode = refused;
            return;
        }

        foreach (EndpointRegistration endpoint in plugin.Registrations.Endpoints.Where(e => e.Public == isPublic))
        {
            if (!string.Equals(endpoint.Method, http.Request.Method, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            RouteValueDictionary values = new();
            if (!new TemplateMatcher(endpoint.Template, values).TryMatch("/" + requestPath, values))
            {
                continue;
            }

            using IDisposable? use = plugin.TryBeginUse();
            if (use is null)
            {
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            if (endpoint.Public)
            {
                PublicCalls.Rewind(http);
            }

            try
            {
                await plugin.MeasureAsync($"{(endpoint.Public ? "public" : "endpoint")}:{endpoint.Method} {endpoint.Pattern}", async () =>
                {
                    // A call that may change something is one unit of work with the server's, and its answer is written
                    // once that's committed.
                    bool transactional = !HttpMethods.IsGet(endpoint.Method) && !HttpMethods.IsHead(endpoint.Method)
                        && PluginCalls.IsTransactional(endpoint.EndpointType);
                    using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, plugin.Stopping);
                    await PluginCalls.RunAsync(http.RequestServices.GetRequiredService<IUnitOfWorkManager>(), plugin, transactional,
                        services => endpoint.Handle(services.GetRequiredService(endpoint.EndpointType), http.Request, values, linked.Token),
                        result => (result ?? Results.NoContent()).ExecuteAsync(http));
                });
            }
            catch (Exception ex) when (!http.Response.HasStarted && !(ex is OperationCanceledException && http.RequestAborted.IsCancellationRequested))
            {
                await WriteErrorAsync(http, plugin.Key, endpoint, ex);
            }

            return;
        }

        http.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    /// <summary>
    /// What a plugin let through, answered as the HTTP API answers it: a host service's refusal (the services catalog)
    /// keeps its status and message (a field you can't filter on is a 403, bad input a 400, no such record a 404), and
    /// anything else is a 500 that says nothing about the inside, logged.
    /// </summary>
    private static async Task WriteErrorAsync(HttpContext http, string key, EndpointRegistration endpoint, Exception ex)
    {
        int status = (int)http.RequestServices.GetRequiredService<IHttpExceptionStatusCodeFinder>().GetStatusCode(http, ex);
        RemoteServiceErrorInfo error = http.RequestServices.GetRequiredService<IExceptionToErrorInfoConverter>().Convert(ex, options =>
        {
            options.SendExceptionsDetailsToClients = false;
            options.SendStackTraceToClients = false;
        });
        if (status >= StatusCodes.Status500InternalServerError)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PluginEndpointDispatcher))
                .LogError(ex, "Plugin {Plugin} endpoint {Method} {Pattern} failed", key, endpoint.Method, endpoint.Pattern);
        }

        http.Response.Clear();
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new RemoteServiceErrorResponse(error), http.RequestAborted);
    }
}

/// <summary>Where a host maps its plugins' endpoints.</summary>
public static class PluginEndpointRoutes
{
    /// <summary>
    /// Maps every enabled plugin's endpoints under <paramref name="prefix"/>: <c>{prefix}/{key}/{path}</c>, any method, the
    /// plugin's own routes inside it.
    /// </summary>
    public static IEndpointConventionBuilder MapPluginEndpoints(this IEndpointRouteBuilder endpoints, string prefix = "/api/plugins") =>
        endpoints.Map($"{prefix.TrimEnd('/')}/{{key}}/{{**path}}", (HttpContext http, string key, string? path) => PluginEndpointDispatcher.HandleAsync(http, key, path));
}
