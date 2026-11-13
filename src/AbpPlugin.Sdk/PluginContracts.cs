using Microsoft.AspNetCore.Http;

namespace AbpPlugin;

public interface IPluginSeeder
{
    Task SeedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Hears an event the distributed way: from the outbox of the module that published it, about a second after
/// the change committed, outside the request. It survives a restart and arrives at least once, so a handler drops what it
/// has already seen (by an id of the event's own). The event says who acted; there's no request to read. A failure
/// is logged and recorded, and isn't retried. It runs in a unit of work of its own (<see cref="PluginUnitOfWorkAttribute"/>).
/// Register a plugin's with <c>AddEventHandler</c>; the host's in DI.
/// </summary>
public interface IEventHandler<in TEvent>
{
    Task HandleAsync(TEvent eventData, CancellationToken cancellationToken);
}

/// <summary>
/// Hears an event the local way: in the same process and request, right after the change commits, so the
/// change is there to read and a reaction can be immediate. It isn't retried, and it's missed if the server stops in
/// between: what must not be missed uses <see cref="IEventHandler{TEvent}"/>. A failure is logged and never undoes the
/// change. It runs in a unit of work of its own (<see cref="PluginUnitOfWorkAttribute"/>). Register a plugin's with <c>AddLocalEventHandler</c>; the host's in DI.
/// </summary>
public interface ILocalEventHandler<in TEvent>
{
    Task HandleAsync(TEvent eventData, CancellationToken cancellationToken);
}

/// <summary>Publishes events to their local and distributed handlers. Provided by the host.</summary>
public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default) where TEvent : notnull;
}

/// <summary>
/// How a plugin's endpoint, recurring job, job handler (each chunk, with its cursor) or event handler commits.
/// Each call is one unit of work with the server: what the plugin saves and what the server's services it
/// calls change commit together when the call ends, or not at all when it throws. Work that keeps its progress as it
/// goes (an import's issues) is marked <c>[PluginUnitOfWork(IsTransactional = false)]</c>: each save then commits by
/// itself, or each batch it opens (<see cref="IPluginUnitsOfWork"/>). A GET endpoint is never transactional.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PluginUnitOfWorkAttribute : Attribute
{
    public bool IsTransactional { get; set; } = true;
}

/// <summary>
/// Units of work a plugin opens itself, for long work that keeps what it has done as it goes: one per batch
/// (a page, an item), each committed when it returns, or undone alone when it throws. The server's services called
/// inside join the batch. Your contexts join it when they come from the batch's services, the first argument; those
/// your class was given were made outside it, and commit by themselves (in non-transactional work) or with the call.
/// </summary>
public interface IPluginUnitsOfWork
{
    Task RunAsync(Func<IServiceProvider, CancellationToken, Task> batch, CancellationToken cancellationToken = default);

    Task<T> RunAsync<T>(Func<IServiceProvider, CancellationToken, Task<T>> batch, CancellationToken cancellationToken = default);
}

/// <summary>A job on a cron schedule. Each run is one unit of work (<see cref="PluginUnitOfWorkAttribute"/>).</summary>
public interface IRecurringJob
{
    /// <summary>The token is cancelled when the plugin is disabled or the server stops.</summary>
    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// An endpoint under /api/plugins/{key}/. A call that may change something (not a GET) is one unit of work
/// (<see cref="PluginUnitOfWorkAttribute"/>). It returns its answer (<see cref="IPluginJson.Result"/>,
/// <see cref="Results"/>, <see cref="PluginResults"/>), which the server writes once the call has committed,
/// so a commit that fails is the caller's error, never a success.
/// </summary>
public interface IPluginEndpoint
{
    Task<IResult> HandleAsync(PluginRequest request, CancellationToken cancellationToken);
}

public enum HealthStatus { Healthy, Degraded, Unhealthy }

public sealed record HealthCheckResult(HealthStatus Status, string? Description = null)
{
    public static HealthCheckResult Healthy(string? description = null) => new(HealthStatus.Healthy, description);
    public static HealthCheckResult Unhealthy(string? description = null) => new(HealthStatus.Unhealthy, description);
}

public interface IPluginHealthCheck
{
    Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Encrypts secrets a plugin stores (tokens, passwords) with the host's Data Protection keys. Scoped to the plugin:
/// one plugin can't read another's secrets.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Null when the value can't be decrypted on this server (for example, the keys changed).</summary>
    string? Unprotect(string protectedValue);
}
