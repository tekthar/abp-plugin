using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AbpPlugin.Packaging;

/// <summary>
/// A countersigning authority's certificate for a publisher's key: it vouches that this key (SubjectPublicKeyInfo,
/// base64) is that publisher's (by its slug, the first part of its plugins' app IDs) from one time to another.
/// </summary>
public sealed record PluginKeyCertificate(
    int Version,
    string Algorithm,
    string Publisher,
    string PublisherName,
    string KeyId,
    string PublicKey,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string IssuerKeyId,
    string Signature);

/// <summary>
/// A countersignature (<see cref="PluginPackageFormat.Countersignature"/>, beside the publisher's signature): an authority,
/// such as a marketplace, checked the publisher's signature against the key it certified and signs the same digest,
/// binding the plugin's app ID, its version and the publisher's signature, with its certificate for the publisher's key
/// in it, so a site needs to trust only the authority's root.
/// </summary>
public sealed record PluginCountersignatureFile(
    int Version,
    string Algorithm,
    string KeyId,
    string Signer,
    string Digest,
    string Listing,
    string ListingVersion,
    string PublisherKeyId,
    string PublisherSignatureSha256,
    PluginKeyCertificate Certificate,
    DateTimeOffset SignedAt,
    string Signature);

/// <summary>What a countersignature showed: none, one by an authority the site doesn't trust, one that doesn't hold, or one that does.</summary>
public enum PluginCountersignatureState
{
    None,
    NotTrusted,
    Broken,
    Holds,
}

/// <summary>A countersignature, checked: its state, why it doesn't hold, and who the certified publisher is when it does.</summary>
public sealed record PluginCountersignatureCheck(PluginCountersignatureState State, string? Problem = null, PluginCountersignatureFile? File = null)
{
    public static readonly PluginCountersignatureCheck None = new(PluginCountersignatureState.None);
}

/// <summary>
/// Countersigning and checking a countersignature: ECDSA over P-256 with SHA-256, as a publisher's signature. Each signed
/// text starts with the format's label (<see cref="PluginPackageFormat.CertificatePrefix"/>,
/// <see cref="PluginPackageFormat.CountersignaturePrefix"/>), so a signature made for one can't pass for another. Only
/// .NET's own types, so a marketplace countersigns with the same code a site checks with.
/// </summary>
public static class PluginCountersignature
{
    public static string FileName => PluginPackageFormat.Current.Countersignature;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>What a key certificate's signature covers.</summary>
    public static byte[] Signed(PluginKeyCertificate c) => Encoding.UTF8.GetBytes(
        $"{PluginPackageFormat.Current.CertificatePrefix}{c.Publisher}\n{c.KeyId}\n{c.PublicKey}\n{c.NotBefore.UtcDateTime:O}\n{c.NotAfter.UtcDateTime:O}\n{c.IssuerKeyId}\n");

    /// <summary>What a countersignature covers.</summary>
    public static byte[] Signed(PluginCountersignatureFile c) => Encoding.UTF8.GetBytes(
        $"{PluginPackageFormat.Current.CountersignaturePrefix}{c.Digest}\n{c.Listing}\n{c.ListingVersion}\n{c.PublisherKeyId}\n{c.PublisherSignatureSha256}\n" +
        $"{c.Certificate.Signature}\n{c.SignedAt.UtcDateTime:O}\n");

    /// <summary>The SHA-256 of a publisher's signature (its base64 text), as a countersignature binds it.</summary>
    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>An authority's certificate for a publisher's public key (SubjectPublicKeyInfo), signed with the authority's private key (PEM).</summary>
    public static PluginKeyCertificate Certify(string authorityPrivatePem, string publisher, string publisherName, byte[] publicKey, DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(authorityPrivatePem);
        PluginKeyCertificate unsigned = new(1, PluginSignature.Algorithm, publisher, publisherName, PluginSignature.KeyId(publicKey), Convert.ToBase64String(publicKey),
            notBefore, notAfter, PluginSignature.KeyId(key.ExportSubjectPublicKeyInfo()), "");
        return unsigned with { Signature = Convert.ToBase64String(key.SignData(Signed(unsigned), HashAlgorithmName.SHA256)) };
    }

    /// <summary>
    /// Countersigns a signed plugin's folder as an authority, writing <see cref="FileName"/> into it: the publisher's
    /// signature must be the certified key's over the folder's files as they are.
    /// </summary>
    public static PluginCountersignatureFile Countersign(string folder, string authorityPrivatePem, string authority, string listing, string version,
        PluginKeyCertificate certificate, DateTimeOffset? at = null)
    {
        PluginSignatureFile publisher = PluginSignature.Read(folder) ?? throw new InvalidOperationException("The plugin isn't signed by its publisher.");
        if (publisher.KeyId != certificate.KeyId || !PluginSignature.Verify(publisher, folder, Convert.FromBase64String(certificate.PublicKey)))
        {
            throw new InvalidOperationException($"{listing} {version}: the publisher's signature isn't their certified key's over these files.");
        }

        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(authorityPrivatePem);
        PluginCountersignatureFile unsigned = new(1, PluginSignature.Algorithm, PluginSignature.KeyId(key.ExportSubjectPublicKeyInfo()), authority, publisher.Digest,
            listing, version, certificate.KeyId, Sha256(publisher.Signature), certificate, at ?? DateTimeOffset.UtcNow, "");
        PluginCountersignatureFile signed = unsigned with { Signature = Convert.ToBase64String(key.SignData(Signed(unsigned), HashAlgorithmName.SHA256)) };
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(signed, Json));
        return signed;
    }

    /// <summary>The folder's countersignature, or null when it has none; one that can't be read throws <see cref="InvalidDataException"/>.</summary>
    public static PluginCountersignatureFile? Read(string folder)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PluginCountersignatureFile>(File.ReadAllText(path), Json) is
                { KeyId: not null, Digest: not null, Signature: not null, Listing: not null, ListingVersion: not null, Certificate.Signature: not null } file
                ? file
                : throw new InvalidDataException($"The plugin's {FileName} can't be read.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"The plugin's {FileName} can't be read.");
        }
    }

    /// <summary>
    /// Checks a folder's countersignature against the roots a site trusts (SubjectPublicKeyInfo each): the whole chain
    /// holds when the countersignature is a root's, its certificate for the publisher's key is a root's and was valid when
    /// the countersignature was made, it binds the publisher's signature, and that signature is the certified key's over
    /// the folder's files as they are. The app ID and version it names are the caller's to compare with the manifest.
    /// </summary>
    public static PluginCountersignatureCheck Check(string folder, PluginSignatureFile? publisher, IReadOnlyList<byte[]> roots)
    {
        PluginCountersignatureFile? c;
        try
        {
            c = Read(folder);
        }
        catch (InvalidDataException ex)
        {
            return new PluginCountersignatureCheck(PluginCountersignatureState.Broken, ex.Message);
        }

        if (c is null)
        {
            return PluginCountersignatureCheck.None;
        }

        PluginCountersignatureCheck Broken(string problem) => new(PluginCountersignatureState.Broken, problem, c);
        byte[]? Root(string keyId) => roots.FirstOrDefault(r => PluginSignature.KeyId(r) == keyId);

        if (Root(c.KeyId) is not { } root)
        {
            return new PluginCountersignatureCheck(PluginCountersignatureState.NotTrusted, $"It's countersigned by {c.Signer} with a key this site doesn't trust (key {c.KeyId}).", c);
        }

        if (c.Version != 1 || c.Algorithm != PluginSignature.Algorithm || !VerifyData(root, Signed(c), c.Signature))
        {
            return Broken($"Its countersignature isn't {c.Signer}'s (key {c.KeyId}): it was changed, or made for something else.");
        }

        PluginKeyCertificate certificate = c.Certificate;
        byte[]? certified = PublicKey(certificate.PublicKey);
        if (Root(certificate.IssuerKeyId) is not { } issuer || certificate.Version != 1 || certificate.Algorithm != PluginSignature.Algorithm
            || certified is null || PluginSignature.KeyId(certified) != certificate.KeyId || !VerifyData(issuer, Signed(certificate), certificate.Signature))
        {
            return Broken($"Its certificate for {certificate.PublisherName}'s key isn't one an authority this site trusts made.");
        }

        if (c.SignedAt < certificate.NotBefore || c.SignedAt > certificate.NotAfter)
        {
            return Broken($"Its certificate for {certificate.PublisherName}'s key wasn't valid when it was countersigned ({c.SignedAt.UtcDateTime:O}).");
        }

        if (publisher is null || c.PublisherKeyId != certificate.KeyId || publisher.KeyId != certificate.KeyId || Sha256(publisher.Signature) != c.PublisherSignatureSha256)
        {
            return Broken("Its countersignature is for another signature than the one it carries.");
        }

        if (c.Digest != publisher.Digest || !PluginSignature.Verify(publisher, folder, certified))
        {
            return Broken("The plugin's files aren't the ones that were countersigned (changed, added or removed since).");
        }

        return new PluginCountersignatureCheck(PluginCountersignatureState.Holds, null, c);
    }

    private static byte[]? PublicKey(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool VerifyData(byte[] publicKey, byte[] data, string signature)
    {
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(data, Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
