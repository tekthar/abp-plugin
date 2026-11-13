using System.Text;
using Volo.Abp.BlobStoring;
using AbpPlugin.Hosting;

namespace AbpPlugin.Tests;

/// <summary>A plugin's own files in storage: under its key, by plain names, readable from anywhere in them.</summary>
public class PluginFilesTests
{
    /// <summary>A store whose files read once, front to back, as a bucket's do.</summary>
    private sealed class Store : IBlobContainer<PluginFilesContainer>
    {
        public readonly Dictionary<string, byte[]> Files = [];

        public async Task SaveAsync(string name, Stream stream, bool overrideExisting = false, CancellationToken cancellationToken = default)
        {
            using MemoryStream copy = new();
            await stream.CopyToAsync(copy, cancellationToken);
            Files[name] = copy.ToArray();
        }

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Files.Remove(name));
        public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Files.ContainsKey(name));
        public Task<Stream> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new OnceThrough(Files[name]));

        public Task<Stream?> GetOrNullAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(Files.TryGetValue(name, out byte[]? bytes) ? new OnceThrough(bytes) : null);
    }

    private sealed class OnceThrough(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    /// <summary>What was recorded of each plugin's files, as the platform's table would have it.</summary>
    private sealed class Ledger : IPluginFileLedger
    {
        public readonly Dictionary<string, (long Bytes, string Sha256)> Files = [];

        public Task SavedAsync(string pluginKey, string name, long bytes, string sha256, CancellationToken cancellationToken)
        {
            Files[$"{pluginKey}/{name}"] = (bytes, sha256);
            return Task.CompletedTask;
        }

        public Task DeletedAsync(string pluginKey, string name, CancellationToken cancellationToken)
        {
            Files.Remove($"{pluginKey}/{name}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_plugins_files_live_under_its_key_and_read_from_anywhere()
    {
        Store store = new();
        Ledger ledger = new();
        PluginFilesFor reports = new(store, ledger, "acme.reports");
        await reports.SaveAsync("monthly.zip", new MemoryStream(Encoding.UTF8.GetBytes("zip bytes")));
        Assert.Equal(["acme.reports/monthly.zip"], store.Files.Keys);
        // Recorded as saved, so backups and the storage copy can list it.
        Assert.Equal((9L, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("zip bytes")))),
            ledger.Files["acme.reports/monthly.zip"]);
        Assert.True(await reports.ExistsAsync("monthly.zip"));

        // A bucket's stream reads once; what a plugin opens can seek (a zip's directory is at its end).
        await using Stream? opened = await reports.OpenReadAsync("monthly.zip");
        Assert.NotNull(opened);
        Assert.True(opened.CanSeek);
        opened.Position = 4;
        Assert.Equal("bytes", await new StreamReader(opened).ReadToEndAsync());

        // Another plugin doesn't see it.
        Assert.False(await new PluginFilesFor(store, ledger, "other").ExistsAsync("monthly.zip"));
        Assert.Null(await reports.OpenReadAsync("missing.zip"));
        Assert.True(await reports.DeleteAsync("monthly.zip"));
        Assert.Empty(store.Files);
        Assert.Empty(ledger.Files);
    }

    [Theory]
    [InlineData("../plugins/x.zip")]
    [InlineData("other-plugin/backup.zip")]
    [InlineData(".hidden")]
    [InlineData("")]
    public async Task A_name_is_never_a_path(string name)
    {
        PluginFilesFor reports = new(new Store(), new Ledger(), "acme.reports");
        await Assert.ThrowsAsync<ArgumentException>(() => reports.ExistsAsync(name));
    }
}
