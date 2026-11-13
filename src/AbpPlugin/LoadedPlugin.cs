using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AbpPlugin;
using Volo.Abp.Uow;
using AbpPlugin.Packaging;

namespace AbpPlugin.Hosting;

/// <summary>
/// One plugin version loaded into its own load context with its own DI container.
/// Everything the host knows about a plugin hangs off this object, so dropping it
/// (plus disposing the containers) is what lets the GC collect the plugin.
/// </summary>
public sealed class LoadedPlugin : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly ServiceProvider _efInternalServices;
    private readonly CancellationTokenSource _stopping = new();
    private int _inFlight;

    private LoadedPlugin(
        PluginManifest manifest,
        string folder,
        PluginLoadContext loadContext,
        ServiceProvider services,
        ServiceProvider efInternalServices,
        PluginRegistrations registrations,
        AbpPlugin.PluginContext context)
    {
        Manifest = manifest;
        Folder = folder;
        LoadContext = loadContext;
        _services = services;
        _efInternalServices = efInternalServices;
        Registrations = registrations;
        Context = context;
        Version = SemanticVersion.Parse(manifest.Version);
    }

    public PluginManifest Manifest { get; }
    public string Key => Manifest.Key;
    public SemanticVersion Version { get; }
    public string Folder { get; }
    internal PluginLoadContext LoadContext { get; }

    /// <summary>
    /// A weak reference to the plugin's load context: alive until its code has unloaded, once the plugin is stopped and
    /// nothing holds its types. For a host's tests and diagnostics; holding the context itself would keep the code loaded.
    /// </summary>
    public WeakReference WatchUnload() => new(LoadContext);
    public PluginRegistrations Registrations { get; }
    public AbpPlugin.PluginContext Context { get; }
    public CancellationToken Stopping => _stopping.Token;
    public ConcurrentDictionary<string, ContributionMetric> Metrics { get; } = new();

    public static LoadedPlugin Load(
        PluginManifest manifest,
        string folder,
        string connectionString,
        IServiceProvider hostServices,
        PluginHostServiceOptions hostServiceOptions)
    {
        if (manifest.Server is null)
        {
            throw new PluginException($"Plugin '{manifest.Key}' has no server section.");
        }

        string mainAssemblyPath = Path.Combine(folder, manifest.Server.Assembly);
        if (!File.Exists(mainAssemblyPath))
        {
            throw new PluginException($"Plugin '{manifest.Key}' assembly '{manifest.Server.Assembly}' was not found in '{folder}'.");
        }

        PluginLoadContext loadContext = new($"plugin:{manifest.Key}@{manifest.Version}", mainAssemblyPath);
        try
        {
            Assembly assembly = loadContext.LoadFromAssemblyPath(mainAssemblyPath);
            Type entryType = assembly.GetType(manifest.Server.Entry, throwOnError: false)
                ?? throw new PluginException($"Entry type '{manifest.Server.Entry}' was not found in '{manifest.Server.Assembly}'.");

            // The host runs AbpPlugin.Sdk's entries itself, and the application's other kinds through their bridges.
            IPluginEntryRunner? runner = typeof(AbpPlugin.PluginEntry).IsAssignableFrom(entryType) ? null
                : PluginHostProfile.Current.EntryRunners.FirstOrDefault(r => r.Runs(entryType))
                    ?? throw new PluginException($"Entry type '{manifest.Server.Entry}' must derive from "
                        + string.Join(" or ", PluginHostProfile.Current.EntryRunners.Select(r => r.BaseName).Append(nameof(AbpPlugin.PluginEntry))) + ".");

            AbpPlugin.PluginContext context = new(
                manifest.Key,
                manifest.Version,
                PluginKeys.SchemaOf(manifest.Key),
                connectionString,
                folder);

            ServiceProvider efInternalServices = new ServiceCollection()
                .AddEntityFrameworkNpgsql()
                .AddLogging()
                .BuildServiceProvider();

            ServiceCollection services = new();
            PluginServiceCollection pluginServices = new(services, context, efInternalServices);
            if (runner is null)
            {
                ((AbpPlugin.PluginEntry)Activator.CreateInstance(entryType)!).ConfigureServices(pluginServices);
            }
            else
            {
                runner.Configure(entryType, pluginServices);
            }

            AddHostBridges(services, context, hostServices, hostServiceOptions);

            ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return new LoadedPlugin(manifest, folder, loadContext, provider, efInternalServices, pluginServices.Registrations, context);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    private static void AddHostBridges(
        IServiceCollection services,
        AbpPlugin.PluginContext context,
        IServiceProvider hostServices,
        PluginHostServiceOptions hostServiceOptions)
    {
        ILoggerFactory loggerFactory = hostServices.GetRequiredService<ILoggerFactory>();
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(context);
        services.AddSingleton<IPluginJson, PluginJson>();
        services.AddSingleton(PluginKeys.ConfigurationOf(hostServices.GetRequiredService<IConfiguration>(), context.Key));

        // The server's open transaction, for the plugin's contexts to join.
        HostTransactions transactions = new(hostServices);
        services.AddScoped(_ => new PluginSharedTransaction(() => transactions.Find(context.ConnectionString)));
        services.AddSingleton<IPluginUnitsOfWork>(sp => new PluginUnitsOfWork(sp.GetRequiredService<IServiceScopeFactory>(),
            hostServices.GetRequiredService<IUnitOfWorkManager>()));

        IServiceScopeFactory hostScopeFactory = hostServices.GetRequiredService<IServiceScopeFactory>();
        services.AddScoped(_ => new HostScopeHolder(hostScopeFactory.CreateScope()));

        // The runtime's services every plugin has: its own secrets, files, settings and the contributions to the
        // extension points it owns, each scoped to its key and made in the host scope tied to the plugin's.
        static IServiceProvider Host(IServiceProvider plugin) => plugin.GetRequiredService<HostScopeHolder>().Services;
        services.AddScoped<ISecretProtector>(sp => new PluginSecretProtector(
            Host(sp).GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(), context.Key));
        services.AddScoped<IPluginFiles>(sp => new PluginFilesFor(
            Host(sp).GetRequiredService<Volo.Abp.BlobStoring.IBlobContainer<PluginFilesContainer>>(), Host(sp).GetRequiredService<IPluginFileLedger>(), context.Key));
        services.AddScoped<IPluginSettings>(sp => new PluginSettingsFor(Host(sp).GetRequiredService<PluginSettingsStore>(), context.Key));
        services.AddScoped<IPluginContributions>(sp => new PluginContributionsFor(Host(sp).GetRequiredService<PluginRuntime>(), context.Key));
        foreach (HostServiceExport export in hostServiceOptions.InEffect())
        {
            services.AddScoped(export.ServiceType, sp => export.Factory(sp.GetRequiredService<HostScopeHolder>().Services, context));
        }
    }

    /// <summary>Marks the plugin as in use until the lease is disposed. Disable waits for leases to drain.</summary>
    public IDisposable? TryBeginUse()
    {
        if (_stopping.IsCancellationRequested)
        {
            return null;
        }

        Interlocked.Increment(ref _inFlight);
        return new UseLease(this);
    }

    public AsyncServiceScope CreateScope() => _services.CreateAsyncScope();

    public async Task<T> MeasureAsync<T>(string contribution, Func<Task<T>> action)
    {
        ContributionMetric metric = Metrics.GetOrAdd(contribution, _ => new ContributionMetric());
        long started = Stopwatch.GetTimestamp();
        try
        {
            T result = await action();
            metric.Record(Stopwatch.GetElapsedTime(started), null);
            return result;
        }
        catch (Exception ex)
        {
            metric.Record(Stopwatch.GetElapsedTime(started), ex.Message);
            throw;
        }
    }

    public Task MeasureAsync(string contribution, Func<Task> action) =>
        MeasureAsync<bool>(contribution, async () => { await action(); return true; });

    /// <summary>Stops new work and waits for in-flight calls, jobs and handlers to finish.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        await _stopping.CancelAsync();
        DateTime deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _inFlight) > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, CancellationToken.None); // the wait has its own deadline; _stopping is already cancelled
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stopping.IsCancellationRequested)
        {
            await StopAsync(TimeSpan.FromSeconds(10));
        }

        await _services.DisposeAsync();
        await _efInternalServices.DisposeAsync();
        _stopping.Dispose();
        Metrics.Clear();
        LoadContext.Unload();
        RuntimeCacheFlusher.ClearCaches();
    }

    private sealed class UseLease(LoadedPlugin plugin) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref plugin._inFlight);
            }
        }
    }
}

/// <summary>Execution history for one contribution: how often it ran, how long it took, the last failure.</summary>
public sealed class ContributionMetric
{
    private long _executions;
    private long _failures;
    private long _totalTicks;

    public long Executions => Interlocked.Read(ref _executions);
    public long Failures => Interlocked.Read(ref _failures);
    public double AverageMs => Executions == 0 ? 0 : TimeSpan.FromTicks(Interlocked.Read(ref _totalTicks) / Executions).TotalMilliseconds;
    public string? LastError { get; private set; }

    public void Record(TimeSpan duration, string? error)
    {
        Interlocked.Increment(ref _executions);
        Interlocked.Add(ref _totalTicks, duration.Ticks);
        if (error is not null)
        {
            Interlocked.Increment(ref _failures);
            LastError = error;
        }
    }
}
