using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;

namespace AbpPlugin.Hosting;

// What a plugin registered in ConfigureServices. The loader wires these up on enable
// and drops them on disable. Only types and strings live here, never instances.

/// <summary>A plugin's handler for an event: local (in the request, after the commit) or distributed (from the outbox).</summary>
public sealed record EventHandlerRegistration(Type EventType, Type HandlerType, bool Local, Func<object, object, CancellationToken, Task> Handle);

/// <summary>A recurring job, and how the host runs it.</summary>
public sealed record RecurringJobRegistration(Type JobType, string Cron, Func<object, CancellationToken, Task> Execute)
{
    public Cronos.CronExpression Expression { get; } =
        Cronos.CronExpression.Parse(Cron, Cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6
            ? Cronos.CronFormat.IncludeSeconds
            : Cronos.CronFormat.Standard);
}

public sealed record ExtensionRegistration(Type ExtensionPointType, Type ImplementationType);

/// <summary>An endpoint: a signed-in caller's, or <see cref="Public"/>, anyone's under <c>public/</c>.</summary>
public sealed record EndpointRegistration(string Method, string Pattern, Type EndpointType, bool Public,
    Func<object, HttpRequest, RouteValueDictionary, CancellationToken, Task<IResult>> Handle)
{
    public RouteTemplate Template { get; } = TemplateParser.Parse(Pattern.TrimStart('/'));
}

/// <summary>A health check, and how the host calls it (the SDK it was built on's interface, through the registration).</summary>
public sealed record HealthCheckRegistration(string Name, Type CheckType, Func<object, CancellationToken, Task<AbpPlugin.HealthCheckResult>> Check);

/// <summary>A seeder, and how the host calls it.</summary>
public sealed record SeederRegistration(Type SeederType, Func<object, CancellationToken, Task> Seed);

/// <summary>A plugin's own check on who sees one of its pages.</summary>
public sealed record PageAccessRegistration(string PageKey, Type AccessType);

public sealed class PluginRegistrations
{
    public List<Type> DbContexts { get; } = [];
    public List<SeederRegistration> Seeders { get; } = [];
    public List<EventHandlerRegistration> EventHandlers { get; } = [];
    public List<RecurringJobRegistration> Jobs { get; } = [];
    public List<ExtensionRegistration> Extensions { get; } = [];
    public List<EndpointRegistration> Endpoints { get; } = [];
    public List<HealthCheckRegistration> HealthChecks { get; } = [];
    public List<PageAccessRegistration> PageAccess { get; } = [];
}
