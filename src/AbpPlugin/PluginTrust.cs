using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using AbpPlugin;
using AbpPlugin.Packaging;

namespace AbpPlugin.Hosting;

/// <summary>A publisher whose plugins the site's admins trust: a name, and the public half of their key.</summary>
public class PluginPublisher
{
    public string KeyId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The key's public half (SubjectPublicKeyInfo).</summary>
    public byte[] PublicKey { get; set; } = [];

    public DateTime AddedAt { get; set; }
    public Guid? AddedBy { get; set; }
}

/// <summary>The site's choice about plugins no key it trusts signed: refused unless its admins allow them.</summary>
public class PluginTrustSettings
{
    public int Id { get; set; } = 1;
    public bool AllowUnsigned { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>What a package's signature showed: a release key, a trusted publisher's, a key the site doesn't know, or none.</summary>
public static class PluginSignatureKinds
{
    public const string Release = "release";

    /// <summary>Countersigned by an authority the site trusts (a marketplace), for the publisher it certified: the signer is that publisher.</summary>
    public const string Marketplace = "marketplace";

    public const string Publisher = "publisher";
    public const string Unknown = "unknown";
    public const string Unsigned = "unsigned";
}

/// <summary>
/// A package's signature, checked: its kind, who signed it (as the key's owner is known here; for a countersigned one, the
/// certified publisher's slug), the key's id, and for a countersigned one, the app ID and version it was countersigned for
/// and the id of the root that countersigned it.
/// </summary>
public sealed record PluginSignatureCheck(string Kind, string? Signer = null, string? KeyId = null, string? Listing = null, string? ListingVersion = null,
    string? RootKeyId = null);

/// <summary>
/// Which plugins a site runs. System plugins, from the image or claiming to be,
/// carry a release key's signature; any other plugin a release key's or that of a publisher the site's admins
/// trust; unsigned ones, or ones signed by a key the site doesn't know, only where the admins allow them (off by
/// default), and they may only where the server lets them (<c>PluginHost:AdminsMayAllowUnsigned</c>, off by
/// default). A package whose files changed after it was signed is never installed. A server adds
/// release keys of its own (<c>PluginHost:ReleaseKeys</c>, a public key each, PEM or base64) and may load unsigned
/// system plugins (<c>PluginHost:AllowUnsignedSystem</c>, as a developer's machine does).
/// </summary>
public sealed class PluginTrust(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<PluginTrust> logger) : ISingletonDependency
{
    private IReadOnlyList<(string KeyId, byte[] Key)>? _release;
    private IReadOnlyList<byte[]>? _roots;
    private HashSet<string>? _builtIn;

    public bool AllowUnsignedSystem => configuration.GetValue("PluginHost:AllowUnsignedSystem", false);

    /// <summary>Whether the site's admins may allow unsigned plugins at all: the server's operator decides.</summary>
    public bool AdminsMayAllowUnsigned => configuration.GetValue("PluginHost:AdminsMayAllowUnsigned", false);

    /// <summary>The release keys this server trusts: the host application's built in (its profile's), and the ones its configuration adds.</summary>
    public IReadOnlyList<(string KeyId, byte[] Key)> ReleaseKeys => _release ??= LoadReleaseKeys();

    /// <summary>Whether the key is one of the host application's own release keys, rather than one the server's operator added.</summary>
    public bool IsBuiltInReleaseKey(string? keyId) =>
        keyId is not null && (_builtIn ??= [.. PluginHostProfile.Current.ReleaseKeys.Select(PluginSignature.ReadPublicKey).OfType<byte[]>().Select(PluginSignature.KeyId)]).Contains(keyId);

    /// <summary>The countersigning authority's roots this server trusts: the application's built in, and the ones its operator lists.</summary>
    public IReadOnlyList<byte[]> CountersignerRoots => _roots ??= LoadRoots();

    private List<byte[]> LoadRoots()
    {
        string setting = PluginHostProfile.Current.CountersignerRootsSetting;
        List<byte[]> roots = [];
        foreach (string text in PluginHostProfile.Current.CountersignerRoots.Concat(configuration.GetSection(setting).Get<string[]>() ?? []).Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            if (PluginSignature.ReadPublicKey(text) is { } key)
            {
                roots.Add(key);
            }
            else
            {
                logger.LogError("A root in {Setting} isn't a P-256 public key (PEM or base64): it's ignored", setting);
            }
        }

        return roots;
    }

    private List<(string KeyId, byte[] Key)> LoadReleaseKeys()
    {
        List<(string, byte[])> keys = [];
        foreach (string text in PluginHostProfile.Current.ReleaseKeys.Concat(configuration.GetSection("PluginHost:ReleaseKeys").Get<string[]>() ?? []).Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            if (PluginSignature.ReadPublicKey(text) is { } key)
            {
                keys.Add((PluginSignature.KeyId(key), key));
            }
            else if (PluginSignature.IsPrivateKey(text))
            {
                logger.LogError("A release key in PluginHost:ReleaseKeys is a private key: it's ignored. Give the server the public half only, and keep the private one off it");
            }
            else
            {
                logger.LogError("A release key in PluginHost:ReleaseKeys isn't a P-256 public key (PEM or base64): it's ignored");
            }
        }

        return keys;
    }

    /// <summary>Checks a plugin folder's signature against the keys trusted here; throws when its files changed after it was signed.</summary>
    public async Task<PluginSignatureCheck> CheckAsync(string folder, CancellationToken cancellationToken)
    {
        PluginSignatureFile? file;
        try
        {
            file = PluginSignature.Read(folder);
        }
        catch (InvalidDataException ex)
        {
            throw new PluginException(ex.Message);
        }

        if (file is null)
        {
            return new PluginSignatureCheck(PluginSignatureKinds.Unsigned);
        }

        if (ReleaseKeys.FirstOrDefault(k => k.KeyId == file.KeyId).Key is { } release)
        {
            return PluginSignature.Verify(file, folder, release) ? new PluginSignatureCheck(PluginSignatureKinds.Release, file.Signer, file.KeyId) : throw Changed();
        }

        // Countersigned by the authority the site trusts: the publisher it certified, whatever key they signed with. One that
        // names a trusted root but doesn't hold is refused outright; one by an authority the site doesn't know is as none.
        PluginCountersignatureCheck counter = PluginCountersignature.Check(folder, file, CountersignerRoots);
        if (counter.State == PluginCountersignatureState.Broken)
        {
            throw new PluginException($"The plugin's countersignature doesn't hold, so it isn't installed. {counter.Problem}");
        }

        if (counter is { State: PluginCountersignatureState.Holds, File: { } countersigned })
        {
            return new PluginSignatureCheck(PluginSignatureKinds.Marketplace, countersigned.Certificate.Publisher, file.KeyId, countersigned.Listing,
                countersigned.ListingVersion, countersigned.KeyId);
        }

        if (await FindPublisherAsync(file.KeyId, cancellationToken) is { } publisher)
        {
            return PluginSignature.Verify(file, folder, publisher.PublicKey) ? new PluginSignatureCheck(PluginSignatureKinds.Publisher, publisher.Name, file.KeyId) : throw Changed();
        }

        // A key the site doesn't know can't be checked, but its digest can: files changed since it was made never go in,
        // whoever allows unsigned plugins.
        return PluginSignature.Matches(file, folder) ? new PluginSignatureCheck(PluginSignatureKinds.Unknown, file.Signer, file.KeyId) : throw Changed();
    }

    private static PluginException Changed() =>
        new("The plugin's files aren't the ones its key signed (changed, added or removed since), so it isn't installed.");

    /// <summary>Whether a system plugin from the server's own folders may load: signed by a release key, or the server allows unsigned ones.</summary>
    public bool MayLoadSystem(PluginSignatureCheck check) => check.Kind == PluginSignatureKinds.Release || AllowUnsignedSystem;

    public static string NotRelease(string key) =>
        $"'{key}' is a system plugin, which only {PluginHostProfile.Current.ProductName}'s release key signs, and this one isn't signed with it. A server built without the release key " +
        "may sign its own with a key it adds to PluginHost:ReleaseKeys, or load unsigned system plugins with PluginHost:AllowUnsignedSystem.";

    /// <summary>
    /// Whether a package under one of the host application's own publishers (<see cref="PluginHostProfile.IsOwn"/>)
    /// may go in: signed by a release key, or unsigned where the server loads
    /// unsigned system plugins (an image its operator built). Never a publisher's key, an unknown one or any other kind,
    /// whatever the admins allow: anyone can name a plugin so.
    /// </summary>
    public bool MayTakeOwn(PluginSignatureCheck check) =>
        check.Kind == PluginSignatureKinds.Release || (check.Kind == PluginSignatureKinds.Unsigned && AllowUnsignedSystem);

    public static string NotOwn(string key, PluginSignatureCheck check) =>
        $"'{key}' is named as one of {PluginHostProfile.Current.ProductName}'s own plugins (the publisher '{AppIds.PublisherOf(key)}'), which only a release key signs, and this one " +
        (check.Kind == PluginSignatureKinds.Unsigned ? "isn't signed" : $"is signed by {check.Signer ?? "someone"} (key {check.KeyId})") +
        ". A plugin of your own goes under your own publisher's name, as acme.reports. A server built without the release key may sign " +
        $"{PluginHostProfile.Current.ProductName}'s plugins with a key it adds to PluginHost:ReleaseKeys, or take them unsigned with PluginHost:AllowUnsignedSystem.";

    /// <summary>
    /// Throws unless the package may be installed, or an installed plugin upgraded to it: one under the application's own
    /// publishers and a system plugin need a release key; any other a key the site trusts, or the admins'
    /// leave for unsigned ones. An upgrade keeps a trusted
    /// signer, whatever the admins allow: a plugin that went in signed by a release key or a trusted
    /// publisher takes another version that key signed, or a release key's, never another publisher's, an unknown key's
    /// or an unsigned one.
    /// </summary>
    public async Task EnsureMayInstallAsync(PluginManifest manifest, PluginSignatureCheck check, PluginInstallation? installed, CancellationToken cancellationToken)
    {
        if (PluginHostProfile.Current.IsOwn(manifest.Key) && !MayTakeOwn(check))
        {
            throw new PluginException(NotOwn(manifest.Key, check));
        }

        if (manifest.IsSystem)
        {
            if (!MayLoadSystem(check))
            {
                throw new PluginException(NotRelease(manifest.Key));
            }

            return;
        }

        if (check.Kind == PluginSignatureKinds.Marketplace)
        {
            string name = PluginHostProfile.Current.CountersignerName;
            if (!string.Equals(check.Listing, manifest.Key, StringComparison.Ordinal) || !string.Equals(check.ListingVersion, manifest.Version, StringComparison.Ordinal))
            {
                throw new PluginException($"'{manifest.Key}' {manifest.Version} carries a countersignature {name} made for {check.Listing} {check.ListingVersion}, so it isn't installed.");
            }

            if (AppIds.PublisherOf(manifest.Key) != check.Signer)
            {
                throw new PluginException($"'{manifest.Key}' is countersigned by {name} for the publisher '{check.Signer}', whose plugins' app IDs start with " +
                                          $"'{check.Signer}.', so it isn't installed.");
            }
        }

        if (SignerChange(manifest, check, installed) is { } change)
        {
            throw new PluginException(change);
        }

        if (check.Kind is PluginSignatureKinds.Release or PluginSignatureKinds.Marketplace or PluginSignatureKinds.Publisher)
        {
            return;
        }

        // Left here: a package no trusted key signed. A plugin with a trusted signer only gets one claiming that same key,
        // no longer trusted: trusting it again lets it in, the admins' leave for unsigned plugins doesn't.
        if (installed is not { SignatureKind: PluginSignatureKinds.Release or PluginSignatureKinds.Marketplace or PluginSignatureKinds.Publisher }
            && await AllowUnsignedAsync(cancellationToken))
        {
            return;
        }

        throw new PluginException(check.Kind == PluginSignatureKinds.Unknown
            ? $"'{manifest.Key}' is signed by {check.Signer} with a key this site doesn't trust (key {check.KeyId}). An admin adds the publisher's key first, " +
              "from the publisher rather than from the package (Plugins, Publishers)." + Countersigned()
            : AdminsMayAllowUnsigned
                ? $"'{manifest.Key}' isn't signed. This site installs plugins signed by {PluginHostProfile.Current.ProductName} or by a publisher its admins trust; an admin can allow unsigned ones (Plugins, Publishers)." + Countersigned()
                : $"'{manifest.Key}' isn't signed. This site installs plugins signed by {PluginHostProfile.Current.ProductName} or by a publisher its admins trust; its server doesn't let admins allow unsigned ones " +
                  "(PluginHost:AdminsMayAllowUnsigned)." + Countersigned());
    }

    /// <summary>What a refusal adds where the server trusts a countersigning authority: its countersigned versions go in.</summary>
    private string Countersigned() =>
        CountersignerRoots.Count > 0 ? $" A version {PluginHostProfile.Current.CountersignerName} countersigned goes in as its publisher's." : "";

    /// <summary>
    /// Why an upgrade to this package would take the plugin from its signer, or null when it doesn't: the installed
    /// version went in signed by a release key or a trusted publisher, and this one is signed by neither a release key
    /// nor that same key. One that went in unsigned, or signed by a key the site didn't know, has no signer to keep.
    /// That same key no longer trusted isn't a change of signer: it's refused as a key the site doesn't trust.
    /// </summary>
    public string? SignerChange(PluginManifest manifest, PluginSignatureCheck check, PluginInstallation? installed)
    {
        if (installed is not { SignatureKind: PluginSignatureKinds.Release or PluginSignatureKinds.Marketplace or PluginSignatureKinds.Publisher, SignerKeyId: { } before }
            || check.Kind == PluginSignatureKinds.Release
            || (check.Kind is PluginSignatureKinds.Publisher or PluginSignatureKinds.Unknown or PluginSignatureKinds.Marketplace && check.KeyId == before)
            // The publisher the authority certified keeps its plugin, whatever key it signs this version with.
            || (installed.SignatureKind == PluginSignatureKinds.Marketplace && check.Kind == PluginSignatureKinds.Marketplace && check.Signer == installed.Signer))
        {
            return null;
        }

        string authority = PluginHostProfile.Current.CountersignerName;
        string from = installed.SignatureKind switch
        {
            PluginSignatureKinds.Marketplace => $"{installed.Signer}, certified by {authority}",
            PluginSignatureKinds.Publisher => installed.Signer ?? before,
            _ => IsBuiltInReleaseKey(before) ? $"{PluginHostProfile.Current.ProductName}'s release key" : $"{installed.Signer ?? before}'s release key",
        };
        string now = check.Kind switch
        {
            PluginSignatureKinds.Marketplace => $"by {check.Signer}, certified by {authority}",
            PluginSignatureKinds.Publisher => $"by {check.Signer}",
            PluginSignatureKinds.Unknown => $"by {check.Signer} with a key this site doesn't trust (key {check.KeyId})",
            _ => "isn't signed",
        };
        return $"'{manifest.Key}' {installed.Version} is signed by {from}, and this version {now}: an upgrade comes from the plugin's own signer, " +
               "whether or not unsigned plugins are allowed.";
    }

    /// <summary>
    /// Whether a plugin's signer is still trusted here: a release key, a publisher still on the list, or for a countersigned
    /// one, the root that countersigned it still among the server's (its operator drops a root that leaked or was replaced).
    /// </summary>
    public async Task<bool> TrustedAsync(PluginInstallation row, CancellationToken cancellationToken) =>
        row.SignerKeyId is { } keyId && (row.SignatureKind == PluginSignatureKinds.Marketplace
            ? IsCountersignerRoot(row.CountersignerKeyId)
            : ReleaseKeys.Any(k => k.KeyId == keyId) || await FindPublisherAsync(keyId, cancellationToken) is not null);

    /// <summary>Whether a root's key id is one of the countersigning roots this server trusts now.</summary>
    public bool IsCountersignerRoot(string? keyId) => keyId is not null && CountersignerRoots.Any(r => PluginSignature.KeyId(r) == keyId);

    /// <summary>Whether unsigned plugins go in now: the admins allow them, where the server lets them.</summary>
    public async Task<bool> AllowUnsignedAsync(CancellationToken cancellationToken)
    {
        if (!AdminsMayAllowUnsigned)
        {
            return false;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPluginsDbContext>().PluginTrustSettings.AsNoTracking()
            .Select(s => s.AllowUnsigned).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The admins' choice; allowing them throws where the server doesn't let admins (stopping always works).</summary>
    public async Task SetAllowUnsignedAsync(bool allow, CancellationToken cancellationToken)
    {
        if (allow && !AdminsMayAllowUnsigned)
        {
            throw new PluginException("This server doesn't let admins allow unsigned plugins. Its operator turns that on " +
                $"({PluginHostProfile.Current.Setting("PluginHost:AdminsMayAllowUnsigned")}).");
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IPluginsDbContext db = scope.ServiceProvider.GetRequiredService<IPluginsDbContext>();
        PluginTrustSettings? settings = await db.PluginTrustSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            db.PluginTrustSettings.Add(settings = new PluginTrustSettings());
        }

        settings.AllowUnsigned = allow;
        settings.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<PluginPublisher>> PublishersAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPluginsDbContext>().PluginPublishers.AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken);
    }

    public async Task<PluginPublisher?> FindPublisherAsync(string keyId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPluginsDbContext>().PluginPublishers.AsNoTracking().FirstOrDefaultAsync(p => p.KeyId == keyId, cancellationToken);
    }

    /// <summary>What the publisher form says of a private key, which the site never keeps (the web says it before sending one).</summary>
    public const string PrivateKeyGiven =
        "That's a private key, which stays with whoever signs: paste their public key (public.pem, \"-----BEGIN PUBLIC KEY-----\"). The site didn't keep it.";

    /// <summary>Trusts a publisher's key: its public half, as the publisher gave it (PEM or base64). Never a private key.</summary>
    public async Task<PluginPublisher> AddPublisherAsync(string? name, string? publicKey, Guid? by, CancellationToken cancellationToken)
    {
        name = name?.Trim();
        if (name is not { Length: > 0 and <= 100 })
        {
            throw new PluginException("Give the publisher a name, up to 100 characters.");
        }

        if (PluginSignature.IsPrivateKey(publicKey))
        {
            throw new PluginException(PrivateKeyGiven);
        }

        byte[] key = PluginSignature.ReadPublicKey(publicKey) ?? throw new PluginException("That isn't a publisher's public key: an EC P-256 key, as PEM or base64.");
        string keyId = PluginSignature.KeyId(key);
        if (ReleaseKeys.Any(k => k.KeyId == keyId))
        {
            throw new PluginException("That's a release key: this server trusts it already.");
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IPluginsDbContext db = scope.ServiceProvider.GetRequiredService<IPluginsDbContext>();
        if (await db.PluginPublishers.AnyAsync(p => p.KeyId == keyId, cancellationToken))
        {
            throw new PluginException($"Key {keyId} is trusted already.");
        }

        PluginPublisher publisher = new() { KeyId = keyId, Name = name, PublicKey = key, AddedAt = DateTime.UtcNow, AddedBy = by };
        db.PluginPublishers.Add(publisher);
        await db.SaveChangesAsync(cancellationToken);
        return publisher;
    }

    /// <summary>Stops trusting a publisher's key: their plugins installed already keep running, and no new version of theirs goes in.</summary>
    public async Task<PluginPublisher?> RemovePublisherAsync(string keyId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IPluginsDbContext db = scope.ServiceProvider.GetRequiredService<IPluginsDbContext>();
        PluginPublisher? publisher = await db.PluginPublishers.FirstOrDefaultAsync(p => p.KeyId == keyId, cancellationToken);
        if (publisher is not null)
        {
            db.PluginPublishers.Remove(publisher);
            await db.SaveChangesAsync(cancellationToken);
        }

        return publisher;
    }
}
