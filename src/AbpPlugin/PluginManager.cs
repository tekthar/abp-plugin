using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AbpPlugin;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Uow;
using AbpPlugin.Packaging;

namespace AbpPlugin.Hosting;

/// <summary>
/// Moves plugins through their lifecycle: install, enable, disable, upgrade, uninstall.
/// One operation at a time; state is persisted so every node (and every restart) agrees.
/// </summary>
public sealed class PluginManager(
    PluginRuntime runtime,
    PluginJobScheduler scheduler,
    PluginPackages packages,
    PluginTrust trust,
    IServiceProvider hostServices,
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    IOptions<PluginHostOptions> options,
    IOptions<AbpCluster.AbpClusterOptions> cluster,
    IEnumerable<IPluginInstallCheck> installChecks,
    IOptions<PluginHostServiceOptions> hostServiceOptions,
    IPluginChanges changes,
    PluginDevelopment development,
    ILogger<PluginManager> logger) : ISingletonDependency
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string ConnectionString => configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

    public bool SafeMode => options.Value.SafeMode;

    /// <summary>A plugin safe mode keeps from loading: any but a system plugin.</summary>
    public bool HeldBack(bool isSystem) => options.Value.SafeMode && !isSystem;

    private static PluginException InSafeMode(string key, string what) =>
        new($"The site started in safe mode: only its system plugins load. '{key}' can be disabled or uninstalled; to {what} it, start the site without safe mode" +
            (PluginHostProfile.Current.SafeModeSetting is { } setting ? $" ({setting})." : "."));

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Servers starting together install the image's system plugins one at a time: the next finds them in
        // the table, rather than both finding none and both adding the row.
        await using IAsyncDisposable held = await AbpCluster.ClusterLocks.TakeAsync(ConnectionString, LockKey("system-plugins"), cancellationToken);
        // A system plugin whose folder isn't signed with a release key isn't installed, upgraded or loaded.
        Dictionary<string, string> refused = new(StringComparer.Ordinal);
        foreach (string folder in options.Value.SystemPluginFolders.Where(Directory.Exists))
        {
            string? key = null;
            try
            {
                PluginManifest manifest = PluginManifest.Load(folder);
                key = manifest.Key;
                PluginSignatureCheck check = await trust.CheckAsync(folder, cancellationToken);
                if (!trust.MayLoadSystem(check))
                {
                    throw new PluginException(PluginTrust.NotRelease(manifest.Key));
                }

                PluginInstallation? existing = await FindAsync(manifest.Key, cancellationToken);
                if (existing is null)
                {
                    logger.LogInformation("Installing system plugin {Key} {Version}", manifest.Key, manifest.Version);
                    await InstallAsync(folder, fromImage: true, cancellationToken);
                }
                else if (SemanticVersion.Parse(manifest.Version) > SemanticVersion.Parse(existing.Version))
                {
                    logger.LogInformation("Upgrading system plugin {Key} {From} -> {To}", manifest.Key, existing.Version, manifest.Version);
                    await UpgradeAsync(manifest.Key, folder, fromImage: true, cancellationToken);
                }
                else if (manifest.Version == existing.Version && Fingerprint(folder) != Fingerprint(existing.InstallPath))
                {
                    // Same version, different package (a rebuild, or a fix shipped without a version bump): nothing of it is
                    // loaded yet at startup, so the stored copy is simply replaced before the plugin starts.
                    logger.LogInformation("Refreshing system plugin {Key} {Version}: its package changed", manifest.Key, manifest.Version);
                    existing.InstallPath = CopyToStore(folder, manifest);
                    existing.Name = manifest.Name;
                    existing.Signed(check);
                    await SaveAsync(existing, isNew: false, cancellationToken, installedFrom: existing.InstallPath);
                }
                else if (existing.SignerKeyId != check.KeyId || existing.SignatureKind != check.Kind)
                {
                    existing.Signed(check);
                    await SaveAsync(existing, isNew: false, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "System plugin in {Folder} could not be installed", folder);
                if (key is not null)
                {
                    refused[key] = ex.Message;
                }
            }
        }

        await RecordManifestsAsync(cancellationToken);
        foreach (PluginInstallation row in await ListAsync(cancellationToken))
        {
            // A site's own plugin from before app IDs: only the application's own were renamed, so it keeps its key.
            if (!AppIds.IsAppId(row.Key))
            {
                logger.LogWarning("Plugin '{Key}' is known by a key that isn't an app ID: it loads and upgrades under that key as before. A version under an app ID would be another plugin.", row.Key);
            }

            if (HeldBack(row.IsSystem))
            {
                logger.LogWarning("Safe mode: plugin {Key} {Version} isn't loaded", row.Key, row.Version);
                continue;
            }

            if (row.IsSystem && refused.TryGetValue(row.Key, out string? why))
            {
                row.State = PluginState.Failed;
                row.LastError = why;
                await SaveAsync(row, isNew: false, cancellationToken);
                continue;
            }

            // System plugins that failed last time get another try on every start.
            if ((row.State == PluginState.Enabled || (row.IsSystem && row.State == PluginState.Failed)) && !runtime.IsEnabled(row.Key))
            {
                try
                {
                    await EnableAsync(row.Key, cancellationToken);
                }
                catch (PluginHeldException ex)
                {
                    logger.LogError("Plugin {Key} isn't loaded: {Why}", row.Key, ex.Message);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Plugin {Key} failed to start", row.Key);
                }
            }
        }
    }

    public async Task StopAllAsync()
    {
        foreach (LoadedPlugin plugin in runtime.Enabled)
        {
            runtime.Deactivate(plugin);
            await plugin.DisposeAsync();
        }
    }

    public async Task<List<PluginInstallation>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IPluginsDbContext db = scope.ServiceProvider.GetRequiredService<IPluginsDbContext>();
        return await db.Plugins.AsNoTracking().OrderBy(p => p.Key).ToListAsync(cancellationToken);
    }

    public async Task<PluginInstallation?> FindAsync(string key, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IPluginsDbContext db = scope.ServiceProvider.GetRequiredService<IPluginsDbContext>();
        return await db.Plugins.AsNoTracking().FirstOrDefaultAsync(p => p.Key == key, cancellationToken);
    }

    /// <summary>
    /// Installs a plugin from a folder or package. One an admin installs is stored as a package, so every node
    /// can load it; a system plugin (<paramref name="fromImage"/>) comes from the image on each node instead.
    /// </summary>
    public async Task<PluginInstallation> InstallAsync(string sourcePath, bool fromImage = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        string? unpacked = null;
        try
        {
            string source = PrepareSource(sourcePath);
            unpacked = source == sourcePath ? null : source;
            PluginManifest manifest = PluginManifest.Load(source);
            if (PluginKeys.Former.TryGetValue(manifest.Key, out string? appId))
            {
                throw new PluginBuiltBeforeException(manifest.Key, appId);
            }

            if (!AppIds.IsAppId(manifest.Key))
            {
                throw new PluginException(PluginKeys.NotAnAppId(manifest.Key));
            }

            CheckRequirements(manifest);
            CheckSettings(manifest);
            EnsureFits(source, manifest);
            if (HeldBack(manifest.IsSystem))
            {
                throw InSafeMode(manifest.Key, "install");
            }

            if (await FindAsync(manifest.Key, cancellationToken) is not null)
            {
                throw new PluginException($"Plugin '{manifest.Key}' is already installed. Upgrade it instead.");
            }

            // Signed by a key the site trusts; a system plugin from the image was checked as the server started.
            PluginSignatureCheck check = await trust.CheckAsync(source, cancellationToken);
            if (!fromImage)
            {
                await trust.EnsureMayInstallAsync(manifest, check, null, cancellationToken);
            }

            string folder = CopyToStore(source, manifest);
            PluginInstallation row = new(Guid.NewGuid(), manifest.Key, manifest.Name, manifest.Version, manifest.IsSystem, folder);
            row.Signed(check);
            if (!fromImage)
            {
                (row.PackageHash, row.PackageBlob) = await packages.StoreAsync(folder, manifest, cancellationToken);
                await PluginPackages.MarkAsync(folder, row.PackageHash, cancellationToken);
            }

            await SaveAsync(row, isNew: true, cancellationToken, installedFrom: folder);

            LoadedPlugin? plugin = null;
            try
            {
                plugin = Load(manifest, folder);
                await PrepareAsync(plugin, runSeeders: true, cancellationToken);
                runtime.Activate(plugin);
                row.State = PluginState.Enabled;
                row.LastError = null;
            }
            catch (Exception ex)
            {
                if (plugin is not null)
                {
                    await ReleaseAsync(plugin);
                }

                row.State = PluginState.Failed;
                row.LastError = ex.Message;
                logger.LogError(ex, "Plugin {Key} failed to install", manifest.Key);
            }

            await SaveAsync(row, isNew: false, cancellationToken);
            changes.Notify();
            return row;
        }
        finally
        {
            // A zip given by path was unpacked to a temporary folder; the store has its own copy now.
            DeleteQuietly(unpacked);
            _gate.Release();
        }
    }

    public async Task<PluginInstallation> EnableAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            PluginInstallation row = await FindAsync(key, cancellationToken) ?? throw new PluginException($"Plugin '{key}' is not installed.");
            if (HeldBack(row.IsSystem))
            {
                throw InSafeMode(key, "enable");
            }

            if (!runtime.IsEnabled(key))
            {
                // Its settings' keys against the plugins enabled since it was turned off.
                CheckSettings(PluginManifest.Load(await LocalFolderAsync(row, cancellationToken)));
                runtime.Activate(await LoadPreparedAsync(row, cancellationToken));
            }

            row.State = PluginState.Enabled;
            row.LastError = null;
            await SaveAsync(row, isNew: false, cancellationToken);
            changes.Notify();
            return row;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PluginInstallation> DisableAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            PluginInstallation row = await FindAsync(key, cancellationToken) ?? throw new PluginException($"Plugin '{key}' is not installed.");
            if (runtime.Find(key) is { } plugin)
            {
                runtime.Deactivate(plugin);
                await ReleaseAsync(plugin);
            }

            row.State = PluginState.Disabled;
            await SaveAsync(row, isNew: false, cancellationToken);
            changes.Notify();
            return row;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads the new version next to the old one, migrates, then swaps it in atomically.
    /// Callers in flight finish on the old version; new calls go to the new one.
    /// </summary>
    public async Task<PluginInstallation> UpgradeAsync(string key, string sourcePath, bool fromImage = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        string? unpacked = null;
        try
        {
            PluginInstallation row = await FindAsync(key, cancellationToken) ?? throw new PluginException($"Plugin '{key}' is not installed.");
            if (HeldBack(row.IsSystem))
            {
                throw InSafeMode(key, "upgrade");
            }

            string source = PrepareSource(sourcePath);
            unpacked = source == sourcePath ? null : source;
            PluginManifest manifest = PluginManifest.Load(source);
            if (manifest.Key != key)
            {
                throw PluginKeys.Current(manifest.Key) == key ? new PluginBuiltBeforeException(manifest.Key, key)
                    : new PluginException($"Package is plugin '{manifest.Key}', not '{key}'.");
            }

            // A Development server takes the installed version again, a rebuild of it; otherwise it must be newer,
            // unless its package is gone from storage (a database moved without the site's storage): the same version's
            // package puts it back, checked as any upgrade is.
            int order = SemanticVersion.Parse(manifest.Version).CompareTo(SemanticVersion.Parse(row.Version));
            bool rebuild = order == 0 && !fromImage && development.SameVersionUpgrades;
            bool restore = order == 0 && !fromImage && !rebuild && await packages.MissingAsync(row, cancellationToken);
            rebuild |= restore;
            if (order < 0 || (order == 0 && !rebuild))
            {
                throw new PluginException($"Version {manifest.Version} is not newer than installed {row.Version}.");
            }

            CheckRequirements(manifest);
            CheckSettings(manifest);
            EnsureFits(source, manifest);
            PluginSignatureCheck check = await trust.CheckAsync(source, cancellationToken);
            if (!fromImage)
            {
                await trust.EnsureMayInstallAsync(manifest, check, row, cancellationToken);
            }

            string folder = CopyToStore(source, manifest, fresh: rebuild);
            (string? Hash, string? Blob) package = (null, null);
            if (!fromImage)
            {
                package = await packages.StoreAsync(folder, manifest, cancellationToken);
                await PluginPackages.MarkAsync(folder, package.Hash!, cancellationToken);
            }

            LoadedPlugin next = Load(manifest, folder);
            try
            {
                await PrepareAsync(next, runSeeders: true, cancellationToken);
            }
            catch
            {
                await ReleaseAsync(next);
                throw;
            }

            LoadedPlugin? previous = runtime.Find(key);
            if (previous is not null)
            {
                runtime.Activate(next); // replaces the previous version in one step
                await ReleaseAsync(previous);
            }
            else if (row.State == PluginState.Failed || (restore && row.State == PluginState.Enabled))
            {
                // Meant to run and couldn't (a package that didn't fit, wasn't the one installed, or was gone from
                // storage): the version that fits runs, here now and on the other servers as they reconcile.
                runtime.Activate(next);
                row.State = PluginState.Enabled;
            }
            else
            {
                await ReleaseAsync(next); // was disabled: files and schema upgraded, stays disabled
            }

            string? replaced = row.PackageBlob;
            row.Version = manifest.Version;
            row.Name = manifest.Name;
            row.InstallPath = folder;
            (row.PackageHash, row.PackageBlob) = package;
            row.Signed(check);
            row.LastError = null;
            await SaveAsync(row, isNew: false, cancellationToken, installedFrom: folder);
            changes.Notify();
            if (replaced is not null && replaced != row.PackageBlob)
            {
                await packages.DeleteAsync(replaced, cancellationToken);
            }

            return row;
        }
        finally
        {
            // A zip given by path was unpacked to a temporary folder; the store has its own copy now.
            DeleteQuietly(unpacked);
            _gate.Release();
        }
    }

    public async Task UninstallAsync(string key, CancellationToken cancellationToken = default)
    {
        PluginInstallation row = await FindAsync(key, cancellationToken) ?? throw new PluginException($"Plugin '{key}' is not installed.");
        if (row.IsSystem)
        {
            throw new PluginException($"'{row.Name}' is a system plugin. It can be disabled but not uninstalled.");
        }

        await DisableAsync(key, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IPluginsDbContext db = scope.ServiceProvider.GetRequiredService<IPluginsDbContext>();
            // Its manifest stays, marked removed, in the same transaction: what it declared is still known while its data is.
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                await db.Plugins.Where(p => p.Key == key).ExecuteDeleteAsync(cancellationToken);
                DateTime now = DateTime.UtcNow;
                await db.PluginManifests.Where(m => m.Key == key)
                    .ExecuteUpdateAsync(m => m.SetProperty(x => x.RemovedAt, now).SetProperty(x => x.UpdatedAt, now), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            changes.Notify();
            await packages.DeleteAsync(row.PackageBlob, cancellationToken);
            // The plugin's schema and data are kept for the retention period; purge is a separate step.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Brings this node in line with the plugins table, for what another node changed, or what couldn't load at
    /// start (storage out of reach): an enabled plugin not running here is loaded; one running another version is swapped
    /// for the one in the table; one disabled or uninstalled stops here too.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        List<PluginInstallation> rows = await ListAsync(cancellationToken);
        foreach (PluginInstallation row in rows)
        {
            try
            {
                LoadedPlugin? running = runtime.Find(row.Key);
                if (HeldBack(row.IsSystem))
                {
                    continue;
                }

                if (row.State == PluginState.Enabled && running is null)
                {
                    await LoadHereAsync(row.Key, cancellationToken);
                }
                else if (row.State == PluginState.Enabled && running is not null && running.Manifest.Version != row.Version)
                {
                    logger.LogInformation("Swapping plugin {Key} {From} for {To}, upgraded elsewhere", row.Key, running.Manifest.Version, row.Version);
                    await ReloadAsync(row.Key, cancellationToken);
                }
                else if (row.State != PluginState.Enabled && running is not null)
                {
                    await StopHereAsync(row.Key);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Plugin {Key} couldn't be brought in line here; it's tried again shortly", row.Key);
            }
        }

        foreach (LoadedPlugin gone in runtime.Enabled.Where(p => rows.All(r => r.Key != p.Key)).ToList())
        {
            await StopHereAsync(gone.Key);
        }
    }

    /// <summary>
    /// Loads a plugin the table has enabled that isn't running here, without changing the table. The reconcile read the
    /// table before it got the gate, so the row is read again: when a disable got there first, nothing is loaded.
    /// </summary>
    private async Task LoadHereAsync(string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await FindAsync(key, cancellationToken) is not { State: PluginState.Enabled } row || runtime.Find(key) is not null)
            {
                return;
            }

            logger.LogInformation("Loading plugin {Key} {Version}, enabled but not running here", row.Key, row.Version);
            runtime.Activate(await LoadPreparedAsync(row, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads the version the table names in place of the one running here, without changing the table (and only while it's enabled there).</summary>
    private async Task ReloadAsync(string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await FindAsync(key, cancellationToken) is not { State: PluginState.Enabled } row || runtime.Find(key)?.Manifest.Version == row.Version)
            {
                return;
            }

            LoadedPlugin next = await LoadPreparedAsync(row, cancellationToken);
            LoadedPlugin? previous = runtime.Find(key);
            runtime.Activate(next);
            if (previous is not null)
            {
                await ReleaseAsync(previous);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops a plugin on this node only (it was disabled or uninstalled elsewhere, and still is under the gate).</summary>
    private async Task StopHereAsync(string key)
    {
        await _gate.WaitAsync();
        try
        {
            if (await FindAsync(key, CancellationToken.None) is not { State: PluginState.Enabled } && runtime.Find(key) is { } plugin)
            {
                logger.LogInformation("Stopping plugin {Key} here: it's disabled or uninstalled", key);
                runtime.Deactivate(plugin);
                await ReleaseAsync(plugin);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads the row's plugin and prepares it (migrations, then seeders, which are idempotent by contract, so running
    /// them on enable also repairs a failed install); released again when preparing fails. The caller activates it.
    /// </summary>
    private async Task<LoadedPlugin> LoadPreparedAsync(PluginInstallation row, CancellationToken cancellationToken)
    {
        string folder = await LocalFolderAsync(row, cancellationToken);
        PluginManifest manifest = PluginManifest.Load(folder);
        try
        {
            // A package made with the plugin's former key, under its row renamed to the app ID: its code names
            // itself by the former key, so it's installed again in its version made with the app ID.
            if (manifest.Key != row.Key && PluginKeys.Current(manifest.Key) == row.Key)
            {
                throw new PluginBuiltBeforeException(manifest.Key, row.Key);
            }

            // Checked as at install: a package installed before the server's upgrade may use what it hasn't
            // got now, such as contracts renamed since; loaded, it would take private copies of them and fail later.
            EnsureFits(folder, manifest);
        }
        catch (PluginException refused)
        {
            // Held everywhere, as a package that isn't the one installed is: Failed, with why, so it isn't tried again
            // until a version that fits is installed, and the plugins page says what to do.
            row.State = PluginState.Failed;
            row.LastError = refused.Message;
            await SaveAsync(row, isNew: false, cancellationToken);
            throw new PluginHeldException(refused.Message);
        }

        CheckRequirements(manifest);
        LoadedPlugin plugin = Load(manifest, folder);
        try
        {
            await PrepareAsync(plugin, runSeeders: true, cancellationToken);
        }
        catch
        {
            await ReleaseAsync(plugin);
            throw;
        }

        return plugin;
    }

    /// <summary>
    /// The plugin's folder on this node: a system plugin's copy from the image; an installed one's package, fetched from
    /// storage and checked against its hash (a package that isn't the one installed fails the plugin, everywhere). One
    /// installed before packages were kept in storage is stored now, from the folder this node has.
    /// </summary>
    private async Task<string> LocalFolderAsync(PluginInstallation row, CancellationToken cancellationToken)
    {
        if (row.PackageHash is not null)
        {
            try
            {
                string folder = await packages.FetchAsync(row, cancellationToken);
                bool storageBack = row.LastError == PluginPackages.NotInStorage(row.Key, row.Version);
                if (row.InstallPath != folder || storageBack)
                {
                    row.InstallPath = folder;
                    row.LastError = storageBack ? null : row.LastError;
                    await SaveAsync(row, isNew: false, cancellationToken);
                }

                return folder;
            }
            catch (PluginPackageException ex) when (ex.Mismatch)
            {
                row.State = PluginState.Failed;
                row.LastError = ex.Message;
                await SaveAsync(row, isNew: false, cancellationToken);
                throw;
            }
            catch (PluginPackageException ex)
            {
                // Not in storage (a database moved without the site's storage): it stays enabled, so it loads by itself
                // once the storage is back, and the plugins page says meanwhile why it isn't running.
                if (row.LastError != ex.Message)
                {
                    row.LastError = ex.Message;
                    await SaveAsync(row, isNew: false, cancellationToken);
                }

                throw;
            }
        }

        if (row.IsSystem)
        {
            return row.InstallPath;
        }

        if (!Directory.Exists(row.InstallPath))
        {
            throw new PluginException($"'{row.Key}' was installed before plugins were kept in storage, and its files aren't on this server: install it again.");
        }

        (row.PackageHash, row.PackageBlob) = await packages.StoreAsync(row.InstallPath, PluginManifest.Load(row.InstallPath), cancellationToken);
        await PluginPackages.MarkAsync(row.InstallPath, row.PackageHash, cancellationToken);
        await SaveAsync(row, isNew: false, cancellationToken);
        logger.LogInformation("Plugin {Key} {Version} is now kept in storage", row.Key, row.Version);
        return row.InstallPath;
    }

    private LoadedPlugin Load(PluginManifest manifest, string folder) =>
        LoadedPlugin.Load(manifest, folder, ConnectionString, hostServices, hostServiceOptions.Value);

    private async Task PrepareAsync(LoadedPlugin plugin, bool runSeeders, CancellationToken cancellationToken)
    {
        // One server at a time migrates and seeds a plugin; the others wait, then find it done.
        await using IAsyncDisposable held = await AbpCluster.ClusterLocks.TakeAsync(ConnectionString, LockKey($"plugin-prepare:{plugin.Manifest.Key}"), cancellationToken);
        // Outside the caller's transaction (an install's request): the plugin's contexts then have connections of their
        // own, as migrations need, and each seed commits by itself.
        using IUnitOfWork apart = hostServices.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true, isTransactional: false);
        await using AsyncServiceScope scope = plugin.CreateScope();
        foreach (Type contextType in plugin.Registrations.DbContexts)
        {
            var db = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);
            await plugin.MeasureAsync($"migrate:{contextType.Name}", () => db.Database.MigrateAsync(cancellationToken));
        }

        foreach (SeederRegistration registration in runSeeders ? plugin.Registrations.Seeders : [])
        {
            object seeder = scope.ServiceProvider.GetRequiredService(registration.SeederType);
            await plugin.MeasureAsync($"seed:{registration.SeederType.Name}", () => registration.Seed(seeder, cancellationToken));
        }

        await apart.CompleteAsync(cancellationToken);
    }

    private async Task ReleaseAsync(LoadedPlugin plugin)
    {
        await plugin.StopAsync(TimeSpan.FromSeconds(10));
        scheduler.Forget(plugin);
        runtime.TrackReleased(plugin);
        await plugin.DisposeAsync();
    }

    /// <summary>The host application's checks on a plugin coming in, against the enabled plugins.</summary>
    private void CheckSettings(PluginManifest manifest)
    {
        foreach (IPluginInstallCheck check in installChecks)
        {
            check.Check(manifest, runtime.Enabled.Select(p => p.Manifest));
        }
    }

    /// <summary>A cluster lock's key, under the application's prefix (AbpCluster's options), as servers of every release take it.</summary>
    private string LockKey(string name) => cluster.Value.KeyPrefix + name;

    private void CheckRequirements(PluginManifest manifest)
    {
        foreach ((string named, string range) in manifest.Requires)
        {
            // A module or a plugin under a name it had before (the profile's former modules and keys) is still found, for one release.
            string dependency = PluginKeys.CurrentRequirement(named);
            string? installed = options.Value.ModuleVersions.GetValueOrDefault(dependency)
                ?? runtime.Find(dependency)?.Manifest.Version;
            if (installed is null)
            {
                throw new PluginException($"'{manifest.Key}' requires '{dependency}', which is not installed or not enabled.");
            }

            if (!SemanticVersion.Parse(installed).Satisfies(range))
            {
                throw new PluginException($"'{manifest.Key}' requires {dependency} {range}, but {installed} is installed.");
            }
        }
    }

    /// <summary>
    /// Refuses a plugin that uses types or members of the application's shared assemblies this server hasn't got, unless it
    /// says it falls back on older servers, when what it lacks is only logged.
    /// </summary>
    private void EnsureFits(string folder, PluginManifest manifest)
    {
        if (manifest.Server is null)
        {
            return;
        }

        List<string> missing = PluginCompatibility.Missing(folder, RunningHost.Instance);
        if (missing.Count == 0)
        {
            return;
        }

        // A plugin that falls back on older servers can't fall back on a newer one: what it lacks was renamed since.
        if (!PluginCompatibility.BuiltBeforeARename(missing) && PluginCompatibility.Tolerates(Path.Combine(folder, manifest.Server.Assembly)))
        {
            logger.LogWarning("Plugin {Key} {Version} uses what this server hasn't got and falls back without it: {Missing}", manifest.Key, manifest.Version,
                string.Join(", ", missing));
            return;
        }

        throw new PluginException(PluginCompatibility.Refusal(manifest.Key, manifest.Version, missing));
    }

    private static void DeleteQuietly(string? folder)
    {
        if (folder is null)
        {
            return;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open (an antivirus scan) leaves it to the system's temporary-file cleanup.
        }
    }

    private static string PrepareSource(string sourcePath)
    {
        if (File.Exists(sourcePath) && PluginPackageFormat.Current.PackageExtensions.Contains(Path.GetExtension(sourcePath), StringComparer.OrdinalIgnoreCase))
        {
            string extracted = Path.Combine(Path.GetTempPath(), "abp-plugin-" + Guid.NewGuid().ToString("N"));
            ZipFile.ExtractToDirectory(sourcePath, extracted);
            return extracted;
        }

        if (!Directory.Exists(sourcePath))
        {
            throw new PluginException($"'{sourcePath}' is not a plugin folder or package.");
        }

        return sourcePath;
    }

    /// <summary>A hash of every file's relative path and bytes; empty when the folder is gone.</summary>
    internal static string Fingerprint(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return string.Empty;
        }

        using System.Security.Cryptography.IncrementalHash hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/')));
            hash.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private string CopyToStore(string source, PluginManifest manifest, bool fresh = false)
    {
        string target = Path.Combine(options.Value.PluginsRoot, manifest.Key, manifest.Version);
        if (fresh)
        {
            // A rebuild of the running version: its own folder, so the one in use keeps every file until it's released.
            target += "-" + DateTime.UtcNow.Ticks;
        }

        if (Directory.Exists(target))
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A previous load of this exact version may still hold files; reuse a fresh folder.
                target += "-" + DateTime.UtcNow.Ticks;
            }
        }

        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }

        return target;
    }

    /// <summary>
    /// Saves a plugin's row; given the folder it was installed from, its manifest and messages too, in the same save
    /// (<see cref="PluginManifestRecord"/>).
    /// </summary>
    private async Task SaveAsync(PluginInstallation row, bool isNew, CancellationToken cancellationToken, string? installedFrom = null)
    {
        row.UpdatedAt = DateTime.UtcNow;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IPluginsDbContext db = scope.ServiceProvider.GetRequiredService<IPluginsDbContext>();
        if (isNew)
        {
            db.Plugins.Add(row);
        }
        else
        {
            db.Plugins.Update(row);
        }

        if (installedFrom is not null)
        {
            await RecordAsync(db, PluginManifestFiles.FromFolder(installedFrom), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task RecordAsync(IPluginsDbContext db, PluginManifestFiles files, CancellationToken cancellationToken)
    {
        PluginManifestRecord? record = await db.PluginManifests.FirstOrDefaultAsync(m => m.Key == files.Key, cancellationToken);
        if (record is null)
        {
            db.PluginManifests.Add(record = new PluginManifestRecord(files.Key));
        }

        record.Take(files);
    }

    /// <summary>
    /// Records the manifest of every installed plugin that has none of its version yet (one installed before manifests
    /// were kept, or upgraded by a server that didn't keep them): from its stored package, which every server reaches,
    /// else its folder. Run at start, inside the start's cluster lock, so servers starting together don't both write it.
    /// </summary>
    private async Task RecordManifestsAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IPluginsDbContext db = scope.ServiceProvider.GetRequiredService<IPluginsDbContext>();
        Dictionary<string, PluginManifestRecord> recorded = await db.PluginManifests.ToDictionaryAsync(m => m.Key, cancellationToken);
        foreach (PluginInstallation row in await db.Plugins.AsNoTracking().ToListAsync(cancellationToken))
        {
            if (recorded.TryGetValue(row.Key, out PluginManifestRecord? record) && record.Version == row.Version && record.RemovedAt is null)
            {
                continue;
            }

            try
            {
                PluginManifestFiles files = row.PackageBlob is not null ? await packages.ReadManifestAsync(row, cancellationToken)
                    : Directory.Exists(row.InstallPath) ? PluginManifestFiles.FromFolder(row.InstallPath)
                    : throw new PluginException($"Neither a package nor the folder {row.InstallPath} is here.");
                await RecordAsync(db, files, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Plugin {Key} {Version}: its manifest couldn't be recorded", row.Key, row.Version);
                db.ChangeTracker.Clear();
            }
        }
    }
}
