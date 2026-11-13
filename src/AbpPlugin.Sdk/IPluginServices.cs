using Microsoft.Extensions.DependencyInjection;

namespace AbpPlugin;

/// <summary>
/// What a plugin can register. Wraps a normal <see cref="IServiceCollection"/> for the
/// plugin's own services and records every registration the host must wire up.
/// </summary>
public interface IPluginServices
{
    /// <summary>The plugin's private service collection, for its own internal services.</summary>
    IServiceCollection Services { get; }

    /// <summary>Registers the plugin's DbContext. Migrations run on install and upgrade, in the plugin's schema.</summary>
    IPluginServices AddDbContext<TContext>() where TContext : PluginDbContext;

    /// <summary>Runs after migrations on install and every upgrade. Must be idempotent.</summary>
    IPluginServices AddSeeder<TSeeder>() where TSeeder : class, IPluginSeeder;

    /// <summary>Hears an event the distributed way, from the publishing module's outbox. Unsubscribed on disable.</summary>
    IPluginServices AddEventHandler<TEvent, THandler>() where THandler : class, IEventHandler<TEvent>;

    /// <summary>Hears an event the local way, in the request right after the change commits. Unsubscribed on disable.</summary>
    IPluginServices AddLocalEventHandler<TEvent, THandler>() where THandler : class, ILocalEventHandler<TEvent>;

    /// <summary>Runs on a cron schedule (seconds field supported). Cancelled on disable.</summary>
    IPluginServices AddRecurringJob<TJob>(string cron) where TJob : class, IRecurringJob;

    /// <summary>Implements an extension point owned by the host or by another plugin.</summary>
    IPluginServices AddExtension<TExtensionPoint, TImplementation>()
        where TExtensionPoint : class
        where TImplementation : class, TExtensionPoint;

    /// <summary>Adds an HTTP endpoint under /api/plugins/{pluginKey}/{pattern}, for signed-in callers. <c>public/</c> is reserved.</summary>
    IPluginServices AddEndpoint<TEndpoint>(string method, string pattern) where TEndpoint : class, IPluginEndpoint;

    /// <summary>
    /// Adds an endpoint anyone may call, signed in or not, under /api/plugins/{pluginKey}/public/{pattern}: for
    /// callers that aren't people, such as a payment provider's webhook, and for browsers coming back from another site.
    /// The plugin checks every call itself (a signature, or asking the caller's system about what it says); the host caps
    /// its body and how often an address and the plugin may be called. It runs as no one: there's no person to act as.
    /// </summary>
    IPluginServices AddPublicEndpoint<TEndpoint>(string method, string pattern) where TEndpoint : class, IPluginEndpoint;


    /// <summary>Adds a check shown on the platform health page.</summary>
    IPluginServices AddHealthCheck<TCheck>(string name) where TCheck : class, IPluginHealthCheck;
}
