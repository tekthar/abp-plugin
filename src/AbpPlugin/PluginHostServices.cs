using Microsoft.Extensions.DependencyInjection;

namespace AbpPlugin.Hosting;

/// <summary>
/// Host services a module exposes to plugins, such as guard's IAccountLookup. Plugins get these and
/// nothing else from the host; they can't resolve or replace arbitrary host services.
/// </summary>
public sealed class PluginHostServiceOptions
{
    internal List<HostServiceExport> Exports { get; } = [];

    /// <summary>Expose a host service. It is resolved from a host scope tied to the plugin's scope.</summary>
    public void Expose<TService>() where TService : class =>
        Exports.Add(new HostServiceExport(typeof(TService), (hostScope, _) => hostScope.GetRequiredService<TService>()));

    /// <summary>Expose a service built per plugin, for example one filtered to what that plugin may see.</summary>
    public void Expose<TService>(Func<IServiceProvider, AbpPlugin.PluginContext, TService> factory) where TService : class =>
        Exports.Add(new HostServiceExport(typeof(TService), (hostScope, plugin) => factory(hostScope, plugin)));

    /// <summary>The host's own fallbacks: plugins get one only when the application exposes no service of its type.</summary>
    internal List<HostServiceExport> Fallbacks { get; } = [];

    /// <summary>Expose a service unless the application exposes its own of the same type (the host's defaults do this).</summary>
    public void ExposeIfNone<TService>(Func<IServiceProvider, AbpPlugin.PluginContext, TService> factory) where TService : class =>
        Fallbacks.Add(new HostServiceExport(typeof(TService), (hostScope, plugin) => factory(hostScope, plugin)));

    /// <summary>What plugins get: every exposed service, and each fallback whose type nothing exposed.</summary>
    internal IEnumerable<HostServiceExport> InEffect() =>
        Exports.Concat(Fallbacks.Where(fallback => Exports.All(export => export.ServiceType != fallback.ServiceType)));
}

internal sealed record HostServiceExport(Type ServiceType, Func<IServiceProvider, AbpPlugin.PluginContext, object> Factory);

/// <summary>A host DI scope that lives exactly as long as the plugin scope that created it.</summary>
internal sealed class HostScopeHolder(IServiceScope scope) : IDisposable, IAsyncDisposable
{
    public IServiceProvider Services => scope.ServiceProvider;

    public void Dispose() => scope.Dispose();

    public ValueTask DisposeAsync() => scope is IAsyncDisposable asyncScope ? asyncScope.DisposeAsync() : ValueTask.CompletedTask;
}
