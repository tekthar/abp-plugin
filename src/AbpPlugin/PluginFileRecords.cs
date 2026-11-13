using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.DependencyInjection;

namespace AbpPlugin.Hosting;

/// <summary>
/// A plugin's file as it was last saved: recorded on every save and removed on delete, since storage
/// can't list what it holds, so the storage copy and backups know the plugin files there are.
/// </summary>
public class PluginFileRecord
{
    public string PluginKey { get; set; } = "";
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTime SavedAt { get; set; }
}

/// <summary>Where plugins' files are recorded as they're saved and deleted.</summary>
public interface IPluginFileLedger
{
    Task SavedAsync(string pluginKey, string name, long bytes, string sha256, CancellationToken cancellationToken);

    Task DeletedAsync(string pluginKey, string name, CancellationToken cancellationToken);
}

/// <summary>The plugin files' record in Postgres (<c>plugin_files</c> in the host's context).</summary>
public sealed class PluginFileLedger(IServiceScopeFactory scopes) : IPluginFileLedger, ISingletonDependency
{
    public async Task SavedAsync(string pluginKey, string name, long bytes, string sha256, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await PluginFileRows.RecordAsync(scope.ServiceProvider.GetRequiredService<IPluginsDbContext>(), pluginKey, name, bytes, sha256, cancellationToken);
    }

    public async Task DeletedAsync(string pluginKey, string name, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IPluginsDbContext>().PluginFiles
            .Where(f => f.PluginKey == pluginKey && f.Name == name).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>The host's record of a plugin's file, in its table as the host application maps it (<see cref="PluginsModel"/>).</summary>
public static class PluginFileRows
{
    /// <summary>Records a file as saved now, replacing what was recorded for its name.</summary>
    public static Task RecordAsync(IPluginsDbContext db, string pluginKey, string name, long bytes, string sha256, CancellationToken cancellationToken)
    {
        // The host application's context is a DbContext (IEfCoreDbContext doesn't give its model).
        IEntityType entity = ((DbContext)db).Model.FindEntityType(typeof(PluginFileRecord))!;
        string table = entity.GetSchema() is { } schema ? $"\"{schema}\".\"{entity.GetTableName()}\"" : $"\"{entity.GetTableName()}\"";
        string sql = "INSERT INTO " + table + @" (""PluginKey"", ""Name"", ""Bytes"", ""Sha256"", ""SavedAt"") VALUES ({0}, {1}, {2}, {3}, {4})
            ON CONFLICT (""PluginKey"", ""Name"") DO UPDATE SET ""Bytes"" = EXCLUDED.""Bytes"", ""Sha256"" = EXCLUDED.""Sha256"", ""SavedAt"" = EXCLUDED.""SavedAt""";
        return db.Database.ExecuteSqlRawAsync(sql, [pluginKey, name, bytes, sha256, DateTime.UtcNow], cancellationToken);
    }
}

/// <summary>A stream read through once, counting its bytes and hashing them (SHA-256) on the way; the stream under it stays open.</summary>
internal sealed class HashingStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long BytesRead { get; private set; }

    /// <summary>The hash of what was read, once it's all been read.</summary>
    public string Sha256() => Convert.ToHexStringLower(_hash.GetCurrentHash());

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Took(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

    public override int Read(Span<byte> buffer) => Took(buffer[..inner.Read(buffer)]);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken);
        return Took(buffer.Span[..read]);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Took(ReadOnlySpan<byte> bytes)
    {
        _hash.AppendData(bytes);
        BytesRead += bytes.Length;
        return bytes.Length;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}

