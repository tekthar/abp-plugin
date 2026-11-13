using AbpPlugin;
using Volo.Abp.BlobStoring;

namespace AbpPlugin.Hosting;

/// <summary>The store plugins' files live in: <c>plugin-files/{plugin key}/{name}</c>.</summary>
[BlobContainerName("plugin-files")]
public sealed class PluginFilesContainer;

/// <summary>
/// <see cref="IPluginFiles"/> for one plugin: its key is the folder, so it never reaches another plugin's files. Each
/// save and delete is recorded (<see cref="PluginFileRecord"/>), so the storage copy and backups can list the files.
/// </summary>
internal sealed class PluginFilesFor(IBlobContainer<PluginFilesContainer> store, IPluginFileLedger ledger, string pluginKey) : IPluginFiles
{
    public async Task SaveAsync(string name, Stream content, CancellationToken cancellationToken = default)
    {
        string path = Path(name);
        // Hashed as it's read into storage, so it's read once.
        await using HashingStream hashed = new(content);
        await store.SaveAsync(path, hashed, overrideExisting: true, cancellationToken);
        await ledger.SavedAsync(pluginKey, name, hashed.BytesRead, hashed.Sha256(), cancellationToken);
    }

    public async Task<Stream?> OpenReadAsync(string name, CancellationToken cancellationToken = default)
    {
        Stream? stream = await store.GetOrNullAsync(Path(name), cancellationToken);
        if (stream is null || stream.CanSeek)
        {
            return stream;
        }

        // A bucket's stream reads once, front to back; a zip is read from its end first.
        await using (stream)
        {
            FileStream local = new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "abp-plugin-file-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            try
            {
                await stream.CopyToAsync(local, cancellationToken);
                local.Position = 0;
                return local;
            }
            catch
            {
                await local.DisposeAsync();
                throw;
            }
        }
    }

    public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) => store.ExistsAsync(Path(name), cancellationToken);

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        bool deleted = await store.DeleteAsync(Path(name), cancellationToken);
        await ledger.DeletedAsync(pluginKey, name, cancellationToken);
        return deleted;
    }

    private string Path(string name) =>
        PluginFileNames.IsValid(name) ? $"{pluginKey}/{name}" : throw new ArgumentException($"\"{name}\" can't be a file's name: use letters, digits, '.', '-' and '_'.", nameof(name));
}
