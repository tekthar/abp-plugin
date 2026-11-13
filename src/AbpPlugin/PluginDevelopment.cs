using Microsoft.Extensions.Hosting;
using Volo.Abp.DependencyInjection;

namespace AbpPlugin.Hosting;

/// <summary>
/// What a Development server does for plugin authors: it takes an upgrade to the
/// version already installed, so a rebuilt plugin goes in without a version bump. None of it outside Development. The
/// signer rules stay as they are: a plugin that went in signed takes only what its signer or a release key signs.
/// </summary>
public sealed class PluginDevelopment(IHostEnvironment environment) : ISingletonDependency
{
    /// <summary>Whether an upgrade may bring the version that's installed again: a rebuild of it.</summary>
    public bool SameVersionUpgrades => environment.IsDevelopment();
}
