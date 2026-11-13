using Volo.Abp.Domain.Entities;

// The host's entity, in the platform's namespace until the host becomes a module of its own.
namespace AbpPlugin.Hosting;

public enum PluginState
{
    Installed,
    Enabled,
    Disabled,
    Failed,
}

public class PluginInstallation : Entity<Guid>
{
    protected PluginInstallation()
    {
    }

    public PluginInstallation(Guid id, string key, string name, string version, bool isSystem, string installPath)
        : base(id)
    {
        Key = key;
        Name = name;
        Version = version;
        IsSystem = isSystem;
        InstallPath = installPath;
        InstalledAt = UpdatedAt = DateTime.UtcNow;
        State = PluginState.Installed;
    }

    public string Key { get; private set; } = null!;
    public string Name { get; set; } = null!;
    public string Version { get; set; } = null!;
    public bool IsSystem { get; set; }
    public PluginState State { get; set; }
    /// <summary>Where it's loaded from on the node that last wrote the row; each node keeps its own copy.</summary>
    public string InstallPath { get; set; } = null!;

    /// <summary>The SHA-256 of its package in storage; null for a system plugin, which comes from the image.</summary>
    public string? PackageHash { get; set; }

    /// <summary>Its package's name in the "plugins" store.</summary>
    public string? PackageBlob { get; set; }

    public DateTime InstalledAt { get; private set; }
    public DateTime UpdatedAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// What its package's signature showed when it went in: a release key, a trusted publisher, a key the site
    /// didn't know, or none; null for one installed before plugins were signed.
    /// </summary>
    public string? SignatureKind { get; set; }

    /// <summary>Who signed it, as the site knew the key then.</summary>
    public string? Signer { get; set; }

    public string? SignerKeyId { get; set; }

    public void Signed(PluginSignatureCheck check) => (SignatureKind, Signer, SignerKeyId) = (check.Kind, check.Signer, check.KeyId);
}
