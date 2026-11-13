using AbpPlugin.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.AspNetCore;
using Volo.Abp.BlobStoring;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;

namespace AbpPlugin;

/// <summary>
/// The plugin host: its services are registered by ABP's conventions, so an application that hosts plugins
/// lists this module in its own module's DependsOn and gives the host its tables (IPluginsDbContext). The rest has
/// defaults for one server (<see cref="PluginHostDefaults"/>), which an application replaces by registering its own:
/// who the caller is, the public endpoints' limits, its own checks on a plugin coming in, notices of changes, recurring
/// jobs' slots, and the events and outgoing calls plugins get. Each server keeps its plugins in line with the plugins
/// table, and runs their recurring jobs.
/// </summary>
[DependsOn(
    typeof(AbpAspNetCoreModule),
    typeof(AbpEntityFrameworkCoreModule),
    typeof(AbpBlobStoringModule),
    typeof(AbpCluster.AbpClusterModule))]
public class AbpPluginModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddPluginHostDefaults();
        Configure<PluginHostServiceOptions>(options =>
        {
            options.ExposeIfNone<AbpPlugin.IEventPublisher>((hostScope, _) => ActivatorUtilities.CreateInstance<PluginLocalEvents>(hostScope));
            options.ExposeIfNone<AbpPlugin.IOutboundHttp>((hostScope, _) => ActivatorUtilities.CreateInstance<PluginOutboundHttp>(hostScope));
        });

        context.Services.AddHostedService<PluginReconciler>();
        context.Services.AddSingleton<PluginJobScheduler>();
        context.Services.AddHostedService(sp => sp.GetRequiredService<PluginJobScheduler>());
    }
}
