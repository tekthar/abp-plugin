using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp.Modularity;

namespace AbpPlugin.Web;

/// <summary>
/// Plugins' web parts: list this module in the application's DependsOn beside <see cref="AbpPluginModule"/>, then
/// <c>app.MapPluginWeb()</c> serves the enabled plugins' browser bundles and lists their remotes for the page's loader.
/// What the application adds to each plugin in that list is its <see cref="IPluginWebSections"/>.
/// </summary>
[DependsOn(typeof(AbpPluginModule))]
public class AbpPluginWebModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddOptions<PluginWebOptions>();
        context.Services.TryAddSingleton<PluginWebRemotes>();
        context.Services.TryAddSingleton<IPluginWebSections, NoPluginWebSections>();
    }
}
