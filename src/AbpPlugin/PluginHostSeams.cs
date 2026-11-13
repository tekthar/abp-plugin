namespace AbpPlugin.Hosting;

/// <summary>
/// Who the host is acting for, as the host application knows it: the account of the request or job, or null
/// for the server itself. A plugin's settings record who changed them by it.
/// </summary>
public interface IPluginCaller
{
    Guid? AccountId { get; }
}

/// <summary>
/// The host application's counted limits, for plugins' public endpoints (<see cref="PublicCalls"/>): takes
/// one from <paramref name="key"/>'s <paramref name="max"/> in each <paramref name="window"/>, counted for the whole
/// site, and says whether there was one to take.
/// </summary>
public interface IPluginCallLimits
{
    Task<bool> TryTakeAsync(string key, int max, TimeSpan window, CancellationToken cancellationToken = default);
}

/// <summary>
/// A check of the host application's on a plugin coming in (installed, upgraded or enabled), against the plugins enabled
/// already: throws <see cref="PluginException"/>, saying what to change, such as two plugins claiming the same page key.
/// </summary>
public interface IPluginInstallCheck
{
    void Check(PluginManifest manifest, IEnumerable<PluginManifest> enabled);
}

/// <summary>Tells the host application that plugins changed (installed, enabled, disabled, upgraded or removed), so open pages reload them.</summary>
public interface IPluginChanges
{
    void Notify();
}

/// <summary>
/// One run of a recurring plugin job for the whole site: claims the named slot for its due time, and says
/// whether this server got it; a second server asking for the same slot doesn't.
/// </summary>
public interface IPluginJobSlots
{
    Task<bool> TryClaimAsync(string name, DateTime slot, CancellationToken cancellationToken = default);
}
