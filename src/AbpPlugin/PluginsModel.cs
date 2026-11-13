using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace AbpPlugin.Hosting;

/// <summary>
/// The plugin host's tables: installations, publishers' keys, the trust switch, plugins' files, manifests as
/// installed and plugins' settings. A host application's context implements this and calls
/// <see cref="PluginsModel.ConfigurePlugins"/>, replacing it (<c>ReplaceDbContext</c>), as AbpWorkflow's
/// <c>IWorkflowsDbContext</c> is replaced; the tables then are in that context's schema and migrations.
/// </summary>
[ConnectionStringName("Default")]
public interface IPluginsDbContext : IEfCoreDbContext
{
    DbSet<PluginInstallation> Plugins { get; }

    DbSet<PluginPublisher> PluginPublishers { get; }

    DbSet<PluginTrustSettings> PluginTrustSettings { get; }

    DbSet<PluginFileRecord> PluginFiles { get; }

    DbSet<PluginManifestRecord> PluginManifests { get; }

    DbSet<PluginSettingRecord> PluginSettings { get; }
}

/// <summary>How the plugin host's tables are mapped: in the host context's default schema, by these names.</summary>
public static class PluginsModel
{
    public static void ConfigurePlugins(ModelBuilder builder)
    {
        builder.Entity<PluginPublisher>(b =>
        {
            b.ToTable("plugin_publishers");
            b.HasKey(p => p.KeyId);
            b.Property(p => p.KeyId).HasMaxLength(16);
            b.Property(p => p.Name).HasMaxLength(100).IsRequired();
            b.Property(p => p.PublicKey).IsRequired();
        });
        builder.Entity<PluginFileRecord>(b =>
        {
            b.ToTable("plugin_files");
            b.HasKey(f => new { f.PluginKey, f.Name });
            b.Property(f => f.PluginKey).HasMaxLength(129);
            b.Property(f => f.Name).HasMaxLength(200);
            b.Property(f => f.Sha256).HasMaxLength(64).IsRequired();
        });
        builder.Entity<PluginSettingRecord>(b =>
        {
            b.ToTable("plugin_settings");
            b.HasKey(s => new { s.PluginKey, s.Key });
            b.Property(s => s.PluginKey).HasMaxLength(129);
            b.Property(s => s.Key).HasMaxLength(AbpPlugin.PluginSettingKeys.MaxLength);
            b.Property(s => s.Value).HasColumnType("jsonb").IsRequired();
        });
        builder.Entity<PluginManifestRecord>(b =>
        {
            b.ToTable("plugin_manifests");
            b.HasKey(m => m.Key);
            b.Property(m => m.Key).HasMaxLength(129);
            b.Property(m => m.Name).HasMaxLength(200).IsRequired();
            b.Property(m => m.Version).HasMaxLength(32).IsRequired();
            b.Property(m => m.Manifest).HasColumnType("jsonb").IsRequired();
            b.Property(m => m.Messages).HasColumnType("jsonb").IsRequired();
        });
        builder.Entity<PluginTrustSettings>(b =>
        {
            b.ToTable("plugin_trust");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).ValueGeneratedNever();
        });
        builder.Entity<PluginInstallation>(b =>
        {
            b.ToTable("plugins");
            b.Property(p => p.SignatureKind).HasMaxLength(16);
            b.Property(p => p.Signer).HasMaxLength(200);
            b.Property(p => p.SignerKeyId).HasMaxLength(16);
            b.HasKey(p => p.Id);
            b.HasIndex(p => p.Key).IsUnique();
            b.Property(p => p.Key).HasMaxLength(129).IsRequired();
            b.Property(p => p.Name).HasMaxLength(200).IsRequired();
            b.Property(p => p.Version).HasMaxLength(32).IsRequired();
            b.Property(p => p.State).HasConversion<string>().HasMaxLength(16);
            b.Property(p => p.InstallPath).HasMaxLength(1024).IsRequired();
            b.Property(p => p.PackageHash).HasMaxLength(64);
            b.Property(p => p.PackageBlob).HasMaxLength(300);
        });
    }
}
