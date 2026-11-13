using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.DependencyInjection;

namespace AbpPlugin.Hosting;

/// <summary>
/// The set of enabled plugins. Everything that calls into plugin code (extensions, events, jobs,
/// endpoints, health checks) reads this snapshot, so disabling a plugin is one removal.
/// </summary>
public sealed class PluginRuntime : ISingletonDependency
{
    private ImmutableDictionary<string, LoadedPlugin> _enabled = ImmutableDictionary<string, LoadedPlugin>.Empty;
    private readonly ConcurrentBag<ReleasedPlugin> _released = [];

    private long _generation;

    public IReadOnlyCollection<LoadedPlugin> Enabled => _enabled.Values.ToArray();

    /// <summary>Changes whenever a plugin is enabled or disabled, so callers can cache what they derive from contributions.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>
    /// Manifest contributions of every enabled plugin to one extension point (an array in the manifest), in plugin
    /// key order. Plain JSON: nothing here references plugin code, so holding it never keeps a plugin loaded.
    /// </summary>
    public IReadOnlyList<PluginContribution> GetContributions(string extensionPoint)
    {
        List<PluginContribution> result = [];
        foreach (LoadedPlugin plugin in Enabled.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (plugin.Manifest.Contributions.TryGetValue(extensionPoint, out System.Text.Json.JsonElement items)
                && items.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
                {
                    result.Add(new PluginContribution(plugin.Key, plugin.Manifest.Version, item.Clone()));
                }
            }
        }

        return result;
    }

    public LoadedPlugin? Find(string key) => _enabled.GetValueOrDefault(key);

    /// <summary>Key, version and install folder of every enabled plugin, for reading files the package ships (texts).</summary>
    public IReadOnlyList<(string Key, string Version, string Folder)> EnabledPackages() =>
        Enabled.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Manifest.Version, p.Folder)).ToList();

    public bool IsEnabled(string key) => _enabled.ContainsKey(key);

    internal void Activate(LoadedPlugin plugin)
    {
        ImmutableInterlocked.AddOrUpdate(ref _enabled, plugin.Key, plugin, (_, _) => plugin);
        Interlocked.Increment(ref _generation);
    }

    internal bool Deactivate(LoadedPlugin plugin)
    {
        bool removed = ImmutableInterlocked.TryRemove(ref _enabled, plugin.Key, out _);
        Interlocked.Increment(ref _generation);
        return removed;
    }

    /// <summary>Remembers a released plugin only through a weak reference, to prove it was collected.</summary>
    internal void TrackReleased(LoadedPlugin plugin) =>
        _released.Add(new ReleasedPlugin(plugin.Key, plugin.Manifest.Version, DateTime.UtcNow, new WeakReference(plugin.LoadContext)));

    /// <summary>Forces collection and reports which released plugin versions are really gone.</summary>
    public IReadOnlyList<UnloadReportItem> GetUnloadReport()
    {
        for (int i = 0; i < 10 && _released.Any(r => r.LoadContext.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return _released
            .OrderBy(r => r.ReleasedAt)
            .Select(r => new UnloadReportItem(r.Key, r.Version, r.ReleasedAt, Collected: !r.LoadContext.IsAlive))
            .ToList();
    }

    /// <summary>
    /// Extension point implementations from enabled plugins plus core implementations from host DI.
    /// Dispose the lease right after use: it holds plugin scopes and keeps the plugin "in use".
    /// </summary>
    public ExtensionLease<T> LeaseExtensions<T>(IServiceProvider hostServices) where T : class
    {
        ExtensionLease<T> lease = new();
        foreach (T core in hostServices.GetServices<T>())
        {
            lease.Add(null, core, null, null);
        }

        foreach (LoadedPlugin plugin in Enabled)
        {
            foreach (ExtensionRegistration registration in plugin.Registrations.Extensions.Where(e => e.ExtensionPointType == typeof(T)))
            {
                IDisposable? use = plugin.TryBeginUse();
                if (use is null)
                {
                    continue;
                }

                AsyncServiceScope scope = plugin.CreateScope();
                T instance = (T)scope.ServiceProvider.GetRequiredService(registration.ImplementationType);
                lease.Add(plugin.Key, instance, scope, use);
            }
        }

        return lease;
    }

    private sealed record ReleasedPlugin(string Key, string Version, DateTime ReleasedAt, WeakReference LoadContext);
}

public sealed record UnloadReportItem(string Key, string Version, DateTime ReleasedAt, bool Collected);

public sealed class ExtensionLease<T> : IAsyncDisposable where T : class
{
    private readonly List<(string? PluginKey, T Instance, AsyncServiceScope? Scope, IDisposable? Use)> _items = [];

    public IEnumerable<(string? PluginKey, T Instance)> Items => _items.Select(i => (i.PluginKey, i.Instance));

    internal void Add(string? pluginKey, T instance, AsyncServiceScope? scope, IDisposable? use) =>
        _items.Add((pluginKey, instance, scope, use));

    public async ValueTask DisposeAsync()
    {
        foreach ((string? _, T _, AsyncServiceScope? scope, IDisposable? use) in _items)
        {
            if (scope is { } s)
            {
                await s.DisposeAsync();
            }

            use?.Dispose();
        }

        _items.Clear();
    }
}

/// <summary>One item a plugin contributed to an extension point in its manifest.</summary>
public sealed record PluginContribution(string PluginKey, string PluginVersion, System.Text.Json.JsonElement Item);
