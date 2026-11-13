using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Uow;

namespace AbpPlugin.Hosting;

/// <summary>
/// Hands an event to the enabled plugins' handlers for its type: the local ones in the request after the
/// change commits, the distributed ones from the outbox, as the application's event bus says which.
/// Each handler runs as one unit of work with the server, unless marked otherwise; a failure is logged and never stops
/// the others.
/// </summary>
public sealed class PluginEventDelivery(PluginRuntime runtime, IUnitOfWorkManager unitOfWorks, ILogger<PluginEventDelivery> logger) : ISingletonDependency
{
    /// <summary>
    /// To each enabled plugin's handler of <paramref name="eventType"/>: the local ones when <paramref name="local"/>,
    /// the distributed ones when not, and both when the event has no outbox to come from (<paramref name="alsoUncataloged"/>).
    /// </summary>
    public async Task DeliverAsync(Type eventType, object eventData, bool local, bool alsoUncataloged, CancellationToken cancellationToken)
    {
        foreach (LoadedPlugin plugin in runtime.Enabled)
        {
            foreach (EventHandlerRegistration registration in plugin.Registrations.EventHandlers
                         .Where(h => h.EventType == eventType && (h.Local == local || (local && alsoUncataloged))))
            {
                using IDisposable? use = plugin.TryBeginUse();
                if (use is null)
                {
                    continue;
                }

                string contribution = $"event:{eventType.Name}:{registration.HandlerType.Name}";
                try
                {
                    using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, plugin.Stopping);
                    await plugin.MeasureAsync(contribution, () =>
                        PluginCalls.RunAsync(unitOfWorks, plugin, PluginCalls.IsTransactional(registration.HandlerType), services =>
                            registration.Handle(services.GetRequiredService(registration.HandlerType), eventData, linked.Token)));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Plugin {Plugin} handler {Handler} failed for {Event}", plugin.Key, registration.HandlerType.Name, eventType.Name);
                }
            }
        }
    }
}
