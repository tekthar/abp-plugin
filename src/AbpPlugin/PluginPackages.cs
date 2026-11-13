using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BlobStoring;
using Volo.Abp.DependencyInjection;
using AbpPlugin.Packaging;

namespace AbpPlugin.Hosting;

/// <summary>Plugin packages an admin installs, in storage: <c>plugins/{key}/{version}/{sha256}.zip</c>.</summary>
[BlobContainerName("plugins")]
public sealed class PluginPackagesContainer;

/// <summary>A package that can't be loaded: its bytes don't match the hash recorded at install, or it isn't in storage.</summary>
public sealed class PluginPackageException(string message, bool mismatch) : PluginException(message)
{
    /// <summary>True when the package was there but wasn't the one installed: it must not run, here or anywhere.</summary>
    public bool Mismatch { get; } = mismatch;
}

/// <summary>
/// Plugin packages in storage. Installing or upgrading stores the package and gives its hash to the plugin's
/// row; each node then keeps its own copy under the plugin cache (<c>{PluginsRoot}/{key}/{version}</c>, with the
/// hash it came from beside it), fetched when it's missing or not the one the row names, and checked before it's
/// unpacked: what runs is what was recorded at install. System plugins come from the image and never go to storage.
/// </summary>
public sealed class PluginPackages(IBlobContainer<PluginPackagesContainer> store, IOptions<PluginHostOptions> options) : ITransientDependency
{
    private static string Marker => PluginPackageFormat.Current.Marker;

    /// <summary>The names the mark had before, still read.</summary>
    private static IReadOnlyList<string> OldMarkers => PluginPackageFormat.Current.FormerMarkers;

    /// <summary>Packs a plugin's folder and stores it; returns the package's hash and its name in the store.</summary>
    public async Task<(string Hash, string Blob)> StoreAsync(string folder, PluginManifest manifest, CancellationToken cancellationToken)
    {
        string zip = Path.Combine(Path.GetTempPath(), $"abp-plugin-package-{Guid.NewGuid():N}.zip");
        try
        {
            await ZipFile.CreateFromDirectoryAsync(folder, zip, CompressionLevel.Optimal, includeBaseDirectory: false, cancellationToken);
            string hash;
            await using (FileStream read = File.OpenRead(zip))
            {
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, cancellationToken));
            }

            string blob = $"{manifest.Key}/{manifest.Version}/{hash}.zip";
            await using (FileStream upload = File.OpenRead(zip))
            {
                await store.SaveAsync(blob, upload, overrideExisting: true, cancellationToken);
            }

            return (hash, blob);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    /// <summary>What a plugin whose package isn't in storage says, in the log and on the plugins page.</summary>
    public static string NotInStorage(string key, string version) =>
        $"The package of '{key}' {version} isn't in storage. Put the site's storage back, or upgrade the plugin with its package, this version or a newer one.";

    /// <summary>
    /// True when the row names a package in storage that isn't there, as when a site's database moved without its
    /// storage: an upgrade with that same version's package puts it back.
    /// </summary>
    public async Task<bool> MissingAsync(PluginInstallation row, CancellationToken cancellationToken) =>
        row.PackageBlob is not null && !await store.ExistsAsync(row.PackageBlob, cancellationToken);

    /// <summary>
    /// The plugin's folder on this node: the cached one when it came from the package the row names, else the package
    /// fetched from storage, checked against the row's hash, and unpacked in its place.
    /// </summary>
    public async Task<string> FetchAsync(PluginInstallation row, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(options.Value.PluginsRoot, row.Key, row.Version);
        if (await MarkedAsync(folder, row.PackageHash, cancellationToken))
        {
            return folder;
        }

        string zip = Path.Combine(Path.GetTempPath(), $"abp-plugin-package-{Guid.NewGuid():N}.zip");
        string unpacked = folder + ".incoming-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (Stream? source = await store.GetOrNullAsync(row.PackageBlob!, cancellationToken))
            {
                if (source is null)
                {
                    throw new PluginPackageException(NotInStorage(row.Key, row.Version), mismatch: false);
                }

                await using FileStream write = File.Create(zip);
                await source.CopyToAsync(write, cancellationToken);
            }

            string hash;
            await using (FileStream read = File.OpenRead(zip))
            {
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, cancellationToken));
            }

            if (!string.Equals(hash, row.PackageHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginPackageException(
                    $"The package of '{row.Key}' {row.Version} in storage isn't the one installed (its hash doesn't match), so it isn't loaded.", mismatch: true);
            }

            await ZipFile.ExtractToDirectoryAsync(zip, unpacked, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(unpacked, Marker), hash, cancellationToken);
            return Replace(folder, unpacked);
        }
        finally
        {
            File.Delete(zip);
            if (Directory.Exists(unpacked))
            {
                Directory.Delete(unpacked, recursive: true);
            }
        }
    }

    /// <summary>
    /// A plugin's manifest and messages from its stored package, which every node reaches, without
    /// unpacking the rest: from this node's folder when it was unpacked from that package, else from storage.
    /// </summary>
    public async Task<PluginManifestFiles> ReadManifestAsync(PluginInstallation row, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(options.Value.PluginsRoot, row.Key, row.Version);
        if (await MarkedAsync(folder, row.PackageHash, cancellationToken))
        {
            return PluginManifestFiles.FromFolder(folder);
        }

        await using Stream source = await store.GetOrNullAsync(row.PackageBlob!, cancellationToken)
            ?? throw new PluginPackageException(NotInStorage(row.Key, row.Version), mismatch: false);
        await using MemoryStream zip = new();
        await source.CopyToAsync(zip, cancellationToken);
        zip.Position = 0;
        return PluginManifestFiles.FromZip(zip);
    }

    /// <summary>Marks a folder as unpacked from a package (when this node installed it, the folder is already there).</summary>
    public static Task MarkAsync(string folder, string hash, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(Path.Combine(folder, Marker), hash, cancellationToken);

    /// <summary>Whether the folder was unpacked from the package with this hash: its mark says so, under its name or a former one.</summary>
    private static async Task<bool> MarkedAsync(string folder, string? hash, CancellationToken cancellationToken)
    {
        foreach (string name in (string[])[Marker, .. OldMarkers])
        {
            string marker = Path.Combine(folder, name);
            if (File.Exists(marker))
            {
                return (await File.ReadAllTextAsync(marker, cancellationToken)).Trim() == hash;
            }
        }

        return false;
    }

    public async Task DeleteAsync(string? blob, CancellationToken cancellationToken)
    {
        if (blob is not null)
        {
            await store.DeleteAsync(blob, cancellationToken);
        }
    }

    /// <summary>
    /// The unpacked copy takes the folder's place, and its path is returned. A folder a loaded version still holds (an
    /// earlier load of the same version) can't go: the copy then goes beside it, as a fresh folder of its own.
    /// </summary>
    private static string Replace(string folder, string unpacked)
    {
        if (Directory.Exists(folder))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                folder += "-" + DateTime.UtcNow.Ticks;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        Directory.Move(unpacked, folder);
        return folder;
    }
}

/// <summary>
/// Keeps each node's plugins in line with the plugins table: every <c>PluginHost:ReconcileSeconds</c> (15),
/// what another node installed, upgraded, disabled or uninstalled happens here too, and what couldn't load at start
/// (storage out of reach) is tried again.
/// </summary>
public sealed class PluginReconciler(PluginManager manager, IConfiguration configuration, ILogger<PluginReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan every = TimeSpan.FromSeconds(Math.Max(5, configuration.GetValue("PluginHost:ReconcileSeconds", 15)));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(every, stoppingToken);
                try
                {
                    await manager.ReconcileAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Bringing plugins in line with the table failed; it runs again shortly.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
