using System.Net.ServerSentEvents;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AbpPlugin;

/// <summary>
/// The call an endpoint answers: the request, the values its pattern matched, and the caller. The endpoint
/// reads it and returns a result; the server commits, then writes the result. Don't write the response from here.
/// </summary>
public sealed class PluginRequest(HttpRequest http, RouteValueDictionary routeValues)
{
    /// <summary>The request itself: its query, headers, body and content type.</summary>
    public HttpRequest Http { get; } = http;

    public RouteValueDictionary RouteValues { get; } = routeValues;

    /// <summary>Who's calling: anyone signed in, or nobody under <c>public/</c>.</summary>
    public ClaimsPrincipal User => Http.HttpContext.User;

    /// <summary>A value the pattern matched ("{project}"), or "" when there's none.</summary>
    public string Route(string name) => RouteValues[name]?.ToString() ?? "";

    /// <summary>A query string value, or "" when there's none.</summary>
    public string Query(string name) => Http.Query[name].ToString();
}

/// <summary>Results for plugin endpoints beyond ASP.NET Core's <see cref="Results"/>. JSON comes from <see cref="IPluginJson.Result"/>.</summary>
public static class PluginResults
{
    /// <summary>
    /// Server-sent events, each written and flushed as it comes, through a proxy too (nginx's buffering is off). The
    /// first is read here, in the call, so what fails before anything is sent (a refusal) is the endpoint's to answer;
    /// the rest stream once the call has committed, so what they write as they go is a unit of work of its own.
    /// </summary>
    public static async Task<IResult> ServerSentEventsAsync(IAsyncEnumerable<SseItem<string>> events, CancellationToken cancellationToken)
    {
        IAsyncEnumerator<SseItem<string>> started = events.GetAsyncEnumerator(cancellationToken);
        bool any;
        try
        {
            any = await started.MoveNextAsync();
        }
        catch
        {
            await started.DisposeAsync();
            throw;
        }

        return new EventsResult(Rest(started, any));
    }

    private static async IAsyncEnumerable<SseItem<string>> Rest(IAsyncEnumerator<SseItem<string>> started, bool any)
    {
        await using (started)
        {
            for (bool more = any; more; more = await started.MoveNextAsync())
            {
                yield return started.Current;
            }
        }
    }

    private sealed class EventsResult(IAsyncEnumerable<SseItem<string>> events) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers["X-Accel-Buffering"] = "no";
            return TypedResults.ServerSentEvents(events).ExecuteAsync(httpContext);
        }
    }
}
