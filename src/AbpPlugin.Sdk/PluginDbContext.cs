using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace AbpPlugin;

/// <summary>
/// Base class for a plugin's DbContext. The loader configures the provider, the connection
/// and the plugin's schema; plugins write normal EF Core code and normal migrations.
/// </summary>
public abstract class PluginDbContext : DbContext
{
    protected PluginDbContext(DbContextOptions options) : base(options)
    {
        Schema = options.FindExtension<PluginSchemaExtension>()?.Schema
            ?? throw new InvalidOperationException(
                "No plugin schema configured. The loader sets it at runtime; design-time factories call UsePluginSchema().");
    }

    /// <summary>The plugin's schema, for example plg_acme__ratings.</summary>
    public string Schema { get; }

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        ConfigureModel(modelBuilder);

        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            string? schema = entityType.GetSchema();
            if (schema is not null && schema != Schema)
            {
                throw new InvalidOperationException(
                    $"Plugin entity '{entityType.DisplayName()}' is mapped to schema '{schema}'. " +
                    $"Plugins may only use their own schema '{Schema}'.");
            }
        }
    }

    protected abstract void ConfigureModel(ModelBuilder modelBuilder);
}

public static class PluginSchemaNames
{
    /// <summary>The longest name Postgres keeps, in bytes.</summary>
    public const int MaxLength = 63;

    /// <summary>
    /// The schema a plugin's tables live in: "plg_" and its app ID with "." written "__" and "-" written "_",
    /// so acme.jira-sync's is plg_acme__jira_sync. A slug holds no "_" and no "--", so "__" comes only from the dot and
    /// no two app IDs share a schema. A name over 63 bytes is "plh_", its first 46 characters, "_" and 12 hex digits of
    /// the app ID's SHA-256. A key from before app IDs keeps the name it had (<see cref="Former"/>). The host may keep a plugin
    /// in a schema it had before, and gives each plugin's context its schema.
    /// </summary>
    public static string For(string pluginKey)
    {
        if (!AppIds.IsAppId(pluginKey))
        {
            return Former(pluginKey);
        }

        string written = pluginKey.Replace(".", "__", StringComparison.Ordinal).Replace('-', '_');
        if ("plg_".Length + written.Length <= MaxLength)
        {
            return "plg_" + written;
        }

        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pluginKey)));
        return "plh_" + written[..46] + "_" + hash[..12];
    }

    /// <summary>The schema a key from before app IDs maps to: "guard-ldap" becomes "plg_acme__ratings".</summary>
    public static string Former(string pluginKey) =>
        "plg_" + new string(pluginKey.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}

public static class PluginDbContextOptionsExtensions
{
    public static TBuilder UsePluginSchema<TBuilder>(this TBuilder builder, string schema) where TBuilder : DbContextOptionsBuilder
    {
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new PluginSchemaExtension(schema));
        return builder;
    }
}

public sealed class PluginSchemaExtension(string schema) : IDbContextOptionsExtension
{
    public string Schema { get; } = schema;

    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(PluginSchemaExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => $"PluginSchema={extension.Schema} ";
        public override int GetServiceProviderHashCode() => extension.Schema.GetHashCode(StringComparison.Ordinal);
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other is ExtensionInfo info && info.Extension is PluginSchemaExtension e && e.Schema == extension.Schema;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["AbpPlugin:PluginSchema"] = extension.Schema;
    }
}
