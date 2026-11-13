using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AbpPlugin;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Uow;

namespace AbpPlugin.Hosting;

/// <summary>A plugin's setting as kept: its app ID, the key, the value as JSON.</summary>
public class PluginSettingRecord
{
    public string PluginKey { get; set; } = "";
    public string Key { get; set; } = "";

    /// <summary>The value's JSON (a <c>jsonb</c> column).</summary>
    public string Value { get; set; } = "";

    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// Why a setting can't be read or kept: a bad key or value, or a plugin that has as many as it may keep
/// (<see cref="TooMany"/>). A 400 through the API.
/// </summary>
public sealed class PluginSettingRefusedException(string message, bool tooMany = false) : Exception(message)
{
    public bool TooMany { get; } = tooMany;
}

/// <summary>
/// Plugins' settings by key, for any plugin: <see cref="PluginSettingsFor"/> hands one plugin its own, and the
/// admin API every installed plugin's. Each call joins the unit of work in progress, so a setting written in a
/// plugin's call commits with it; outside one, each write commits by itself.
/// </summary>
public sealed class PluginSettingsStore(IUnitOfWorkManager unitsOfWork, IDbContextProvider<IPluginsDbContext> provider, IPluginCaller caller)
    : ITransientDependency
{
    public Task<PluginSetting?> GetAsync(string pluginKey, string key, CancellationToken cancellationToken) =>
        RunAsync(async db =>
        {
            Check(key);
            PluginSettingRecord? row = await db.PluginSettings.AsNoTracking().FirstOrDefaultAsync(s => s.PluginKey == pluginKey && s.Key == key, cancellationToken);
            return row is null ? null : ToSetting(row);
        });

    public Task<IReadOnlyList<PluginSetting>> ListAsync(string pluginKey, string? prefix, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<PluginSetting>>(async db =>
        {
            prefix ??= "";
            if (prefix.Length > PluginSettingKeys.MaxLength || (prefix.Length > 0 && !PluginSettingKeys.IsValid(prefix)))
            {
                throw new PluginSettingRefusedException($"\"{prefix}\" can't begin a setting's key.");
            }

            IQueryable<PluginSettingRecord> rows = db.PluginSettings.AsNoTracking().Where(s => s.PluginKey == pluginKey);
            if (prefix.Length > 0)
            {
                // Compared as written: LIKE would read '_' as any character.
                rows = rows.Where(s => s.Key.Substring(0, prefix.Length) == prefix);
            }

            return [.. (await rows.OrderBy(s => s.Key).ToListAsync(cancellationToken)).Select(ToSetting)];
        });

    /// <summary>Sets (or replaces) a setting, as the signed-in person whose call it is, if any.</summary>
    public Task<PluginSetting> SetAsync(string pluginKey, string key, JsonElement value, CancellationToken cancellationToken) =>
        RunAsync(async db =>
        {
            Check(key);
            if (PluginSettingKeys.ValueProblem(value) is { } problem)
            {
                throw new PluginSettingRefusedException(problem);
            }

            if (!await db.PluginSettings.AnyAsync(s => s.PluginKey == pluginKey && s.Key == key, cancellationToken)
                && await db.PluginSettings.CountAsync(s => s.PluginKey == pluginKey, cancellationToken) >= PluginSettingKeys.MaxPerPlugin)
            {
                throw new PluginSettingRefusedException($"A plugin keeps at most {PluginSettingKeys.MaxPerPlugin} settings; delete some first, or keep data in tables of its own.", tooMany: true);
            }

            string json = value.GetRawText();
            DateTime now = DateTime.UtcNow;
            // The account whose rights the call has: none in a job or the worker.
            Guid? by = caller.AccountId;
            // One statement, so two writers of the same key can't both insert it; in the table where the host
            // application's context maps the settings.
            await db.Database.ExecuteSqlRawAsync($$"""
                INSERT INTO {{TableOf(db)}} ("PluginKey", "Key", "Value", "UpdatedAt", "UpdatedBy")
                VALUES ({0}, {1}, {2}::jsonb, {3}, {4}::uuid)
                ON CONFLICT ("PluginKey", "Key") DO UPDATE SET "Value" = EXCLUDED."Value", "UpdatedAt" = EXCLUDED."UpdatedAt", "UpdatedBy" = EXCLUDED."UpdatedBy"
                """, [pluginKey, key, json, now, by], cancellationToken);
            return new PluginSetting(key, value.Clone(), now, by);
        });

    /// <summary>Whether a plugin by this app ID is installed, enabled or not.</summary>
    public Task<bool> InstalledAsync(string pluginKey, CancellationToken cancellationToken) =>
        RunAsync(db => db.Plugins.AnyAsync(p => p.Key == pluginKey, cancellationToken));

    /// <summary>Removes a setting; false when there was none.</summary>
    public Task<bool> DeleteAsync(string pluginKey, string key, CancellationToken cancellationToken) =>
        RunAsync(async db =>
        {
            Check(key);
            return await db.PluginSettings.Where(s => s.PluginKey == pluginKey && s.Key == key).ExecuteDeleteAsync(cancellationToken) > 0;
        });

    /// <summary>The settings' table as the context maps it: its schema (if any) and name, quoted.</summary>
    private static string TableOf(IPluginsDbContext db)
    {
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity = db.PluginSettings.EntityType;
        string table = '"' + entity.GetTableName()!.Replace("\"", "\"\"") + '"';
        return entity.GetSchema() is { Length: > 0 } schema ? '"' + schema.Replace("\"", "\"\"") + "\"." + table : table;
    }

    private static void Check(string key)
    {
        if (PluginSettingKeys.Problem(key) is { } problem)
        {
            throw new PluginSettingRefusedException(problem);
        }
    }

    private static PluginSetting ToSetting(PluginSettingRecord row)
    {
        using JsonDocument document = JsonDocument.Parse(row.Value);
        return new PluginSetting(row.Key, document.RootElement.Clone(), DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc), row.UpdatedBy);
    }

    /// <summary>The work in the unit of work in progress, or in one of its own when there's none.</summary>
    private async Task<T> RunAsync<T>(Func<IPluginsDbContext, Task<T>> work)
    {
        using IUnitOfWork uow = unitsOfWork.Begin(requiresNew: false);
        T result = await work(await provider.GetDbContextAsync());
        await uow.CompleteAsync();
        return result;
    }
}

/// <summary>
/// <see cref="IPluginSettings"/> for one plugin: its app ID is the scope, so it never reaches another plugin's settings.
/// A bad key or value is the plugin's mistake, so it's an <see cref="ArgumentException"/> there; one setting too many is an
/// <see cref="InvalidOperationException"/>.
/// </summary>
internal sealed class PluginSettingsFor(PluginSettingsStore store, string pluginKey) : IPluginSettings
{
    public async Task<JsonElement?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        (await Refused(() => store.GetAsync(pluginKey, key, cancellationToken), nameof(key)))?.Value;

    public Task SetAsync(string key, JsonElement value, CancellationToken cancellationToken = default) =>
        Refused(() => store.SetAsync(pluginKey, key, value, cancellationToken), nameof(value));

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        Refused(() => store.DeleteAsync(pluginKey, key, cancellationToken), nameof(key));

    public Task<IReadOnlyList<PluginSetting>> ListAsync(string? prefix = null, CancellationToken cancellationToken = default) =>
        Refused(() => store.ListAsync(pluginKey, prefix, cancellationToken), nameof(prefix));

    private static async Task<T> Refused<T>(Func<Task<T>> work, string parameter)
    {
        try
        {
            return await work();
        }
        catch (PluginSettingRefusedException ex) when (ex.TooMany)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (PluginSettingRefusedException ex)
        {
            throw new ArgumentException(ex.Message, parameter, ex);
        }
    }
}
