using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp.Uow;
using Volo.Abp.Users;

namespace AbpPlugin.Hosting;

/// <summary>
/// The host's seams as a host that gives none of its own has them: the module registers these first,
/// and an application's own registration of a seam, made after, is the one used.
/// </summary>
public static class PluginHostDefaults
{
    /// <summary>The seams' defaults, each only where the application hasn't registered the seam already.</summary>
    public static IServiceCollection AddPluginHostDefaults(this IServiceCollection services)
    {
        services.TryAddTransient<IPluginCaller, CurrentUserPluginCaller>();
        services.TryAddSingleton<IPluginCallLimits, InMemoryPluginCallLimits>();
        services.TryAddTransient<IPluginInstallCheck, NoPluginInstallCheck>();
        services.TryAddTransient<IPluginChanges, NoPluginChanges>();
        services.TryAddSingleton<IPluginJobSlots, OneServerPluginJobSlots>();
        services.AddHttpClient(PluginOutboundHttp.ClientName);
        return services;
    }
}

/// <summary>Who the host acts for: ABP's current user, or nobody for the server itself.</summary>
public sealed class CurrentUserPluginCaller(ICurrentUser user) : IPluginCaller
{
    public Guid? AccountId => user.Id;
}

/// <summary>
/// Plugins' public endpoints' limits counted in this server's memory: right for one server. A host of several servers
/// counts them together and registers that instead.
/// </summary>
public sealed class InMemoryPluginCallLimits : IPluginCallLimits
{
    private readonly Dictionary<string, (DateTime Start, int Taken)> _windows = new(StringComparer.Ordinal);

    public Task<bool> TryTakeAsync(string key, int max, TimeSpan window, CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;
        lock (_windows)
        {
            (DateTime start, int taken) = _windows.TryGetValue(key, out var current) && now - current.Start < window ? current : (now, 0);
            if (taken >= max)
            {
                _windows[key] = (start, taken);
                return Task.FromResult(false);
            }

            _windows[key] = (start, taken + 1);
            return Task.FromResult(true);
        }
    }
}

/// <summary>No check of the application's on a plugin coming in: the host's own checks are all there are.</summary>
public sealed class NoPluginInstallCheck : IPluginInstallCheck
{
    public void Check(PluginManifest manifest, IEnumerable<PluginManifest> enabled)
    {
    }
}

/// <summary>Nobody to tell that plugins changed.</summary>
public sealed class NoPluginChanges : IPluginChanges
{
    public void Notify()
    {
    }
}

/// <summary>
/// A recurring job's slot claimed in this server's memory: right for one server, where each slot is claimed once. A host
/// of several servers claims them where all of them see it and registers that instead.
/// </summary>
public sealed class OneServerPluginJobSlots : IPluginJobSlots
{
    private readonly Dictionary<string, DateTime> _claimed = new(StringComparer.Ordinal);

    public Task<bool> TryClaimAsync(string name, DateTime slot, CancellationToken cancellationToken = default)
    {
        lock (_claimed)
        {
            if (_claimed.TryGetValue(name, out DateTime last) && slot <= last)
            {
                return Task.FromResult(false);
            }

            _claimed[name] = slot;
            return Task.FromResult(true);
        }
    }
}

/// <summary>
/// A plugin's events in a host with no event catalog of its own: each goes to the enabled plugins' handlers of its type,
/// local and distributed alike, once the host's unit of work completes (at once when there is none).
/// </summary>
public sealed class PluginLocalEvents(PluginEventDelivery delivery, IUnitOfWorkManager unitOfWorks) : AbpPlugin.IEventPublisher
{
    public Task PublishAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default) where TEvent : notnull
    {
        if (unitOfWorks.Current is { } unitOfWork)
        {
            unitOfWork.OnCompleted(() => delivery.DeliverAsync(typeof(TEvent), eventData, local: true, alsoUncataloged: true, CancellationToken.None));
            return Task.CompletedTask;
        }

        return delivery.DeliverAsync(typeof(TEvent), eventData, local: true, alsoUncataloged: true, cancellationToken);
    }
}

/// <summary>A plugin's outgoing calls in a host with no rules of its own for them: the HTTP client factory's client.</summary>
public sealed class PluginOutboundHttp(IHttpClientFactory clients) : AbpPlugin.IOutboundHttp
{
    public const string ClientName = "AbpPlugin.Outbound";

    public HttpClient Client() => clients.CreateClient(ClientName);
}
