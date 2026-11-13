using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AbpPlugin.Packaging;

/// <summary>
/// What a signed plugin carries beside its files: who signed it, the key's id, a digest of every file, and
/// the signature over that digest. The key's public half isn't in it: a site checks against the keys it trusts.
/// </summary>
public sealed record PluginSignatureFile(int Version, string Algorithm, string KeyId, string Signer, string Digest, string Signature, DateTimeOffset SignedAt);

/// <summary>
/// Signing and checking plugin packages: ECDSA over P-256 with SHA-256, built into .NET on
/// every platform. The digest covers each file's path and bytes, the manifest included, so a changed, added or removed
/// file breaks it. Only .NET's own types here (AbpPlugin.Packaging), so the signing tool and a marketplace use it too.
/// </summary>
public static class PluginSignature
{
    /// <summary>The signature's file name, in the current format.</summary>
    public static string FileName => PluginPackageFormat.Current.Signature;

    public const string Algorithm = "ECDSA-P256-SHA256";

    /// <summary>What a signature is made over, before the digest, in the current format (<see cref="PluginPackageFormat.SignedPrefix"/>).</summary>
    public static string SignedPrefix => PluginPackageFormat.Current.SignedPrefix;

    /// <summary>Files the digest leaves out: the signature itself, and the mark a server writes on a package it unpacked (<c>PluginPackages</c>).</summary>
    private static string[] NotSigned =>
        [FileName, PluginPackageFormat.Current.Marker, .. PluginPackageFormat.Current.FormerMarkers, .. PluginPackageFormat.Current.Unsigned];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>A key's id: the start of the SHA-256 of its public half (SubjectPublicKeyInfo), as people compare it.</summary>
    public static string KeyId(byte[] publicKey) => Convert.ToHexStringLower(SHA256.HashData(publicKey))[..16];

    /// <summary>A new key pair: the private half as PKCS#8 PEM, the public half as SubjectPublicKeyInfo PEM, and its id.</summary>
    public static (string PrivatePem, string PublicPem, string KeyId) CreateKey()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] publicKey = key.ExportSubjectPublicKeyInfo();
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem(), KeyId(publicKey));
    }

    /// <summary>
    /// A public key as a site keeps it (SubjectPublicKeyInfo bytes), from PEM or base64; null when it isn't a P-256
    /// public key. A private key's PEM is refused too, though its public half could be read from it: a site is never
    /// given one (<see cref="IsPrivateKey"/> says which it was).
    /// </summary>
    public static byte[]? ReadPublicKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsPrivateKey(text))
        {
            return null;
        }

        try
        {
            string trimmed = text.Trim();
            byte[] spki;
            if (trimmed.StartsWith("-----", StringComparison.Ordinal))
            {
                // Not ImportFromPem: it takes a private key's PEM as well.
                if (!PemEncoding.TryFind(trimmed, out PemFields pem) || trimmed[pem.Label] != "PUBLIC KEY")
                {
                    return null;
                }

                spki = Convert.FromBase64String(trimmed[pem.Base64Data]);
            }
            else
            {
                spki = Convert.FromBase64String(trimmed);
            }

            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            return key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value ? key.ExportSubjectPublicKeyInfo() : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the text holds a private key: a PEM labelled as one anywhere in it (PKCS#8, encrypted or not, EC or RSA),
    /// or an EC or RSA private key as base64.
    /// </summary>
    public static bool IsPrivateKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> rest = text;
        while (PemEncoding.TryFind(rest, out PemFields pem))
        {
            if (rest[pem.Label].EndsWith("PRIVATE KEY", StringComparison.Ordinal))
            {
                return true;
            }

            rest = rest[pem.Location.End..];
        }

        if (text.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
        {
            return true; // a PEM too broken to parse, labelled as a private key all the same
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        using ECDsa ec = ECDsa.Create();
        using RSA rsa = RSA.Create();
        return Imports(() => ec.ImportPkcs8PrivateKey(bytes, out _)) || Imports(() => ec.ImportECPrivateKey(bytes, out _))
            || Imports(() => rsa.ImportPkcs8PrivateKey(bytes, out _)) || Imports(() => rsa.ImportRSAPrivateKey(bytes, out _));

        static bool Imports(Action import)
        {
            try
            {
                import();
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// What the digest covers: each signed file's path (with '/') and SHA-256 (lowercase hex), in ordinal order of path.
    /// A phone checks a plugin's files against it one by one.
    /// </summary>
    public static List<(string Path, string Sha256)> Listing(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Relative: Path.GetRelativePath(folder, f).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(f => !NotSigned.Contains(f.Relative, StringComparer.Ordinal))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .Select(f => (f.Relative, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f.Full)))))
            .ToList();

    /// <summary>The digest of a plugin's folder: the SHA-256 of its listing, a "path\nsha256\n" line pair per file.</summary>
    public static string Digest(string folder) => Digest(Listing(folder));

    /// <summary>The digest of a listing, as <see cref="Digest(string)"/> makes it from a folder.</summary>
    public static string Digest(IEnumerable<(string Path, string Sha256)> listing)
    {
        StringBuilder text = new();
        foreach ((string path, string sha256) in listing)
        {
            text.Append(path).Append('\n').Append(sha256).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Signs a plugin's folder with a private key (PEM), writing <see cref="FileName"/> into it.</summary>
    public static PluginSignatureFile Sign(string folder, string privatePem, string signer, DateTimeOffset? at = null)
    {
        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        string digest = Digest(folder);
        PluginSignatureFile file = new(1, Algorithm, KeyId(key.ExportSubjectPublicKeyInfo()), signer, digest,
            Convert.ToBase64String(key.SignData(Signed(digest), HashAlgorithmName.SHA256)), at ?? DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(file, Json));
        return file;
    }

    /// <summary>The folder's signature file, or null when it has none; one that can't be read throws <see cref="InvalidDataException"/>.</summary>
    public static PluginSignatureFile? Read(string folder)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PluginSignatureFile>(File.ReadAllText(path), Json) is { KeyId: not null, Digest: not null, Signature: not null } file
                ? file
                : throw new InvalidDataException($"The plugin's {Path.GetFileName(path)} can't be read.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"The plugin's {Path.GetFileName(path)} can't be read.");
        }
    }

    /// <summary>
    /// Whether the file's digest is that of the folder's files as they are now. Without the key this shows only that
    /// nothing changed since the digest was made, not who made it.
    /// </summary>
    public static bool Matches(PluginSignatureFile file, string folder) =>
        file.Version == 1 && file.Algorithm == Algorithm && string.Equals(Digest(folder), file.Digest, StringComparison.Ordinal);

    /// <summary>Whether the signature is the key's, over the folder's files as they are now.</summary>
    public static bool Verify(PluginSignatureFile file, string folder, byte[] publicKey)
    {
        if (!Matches(file, folder))
        {
            return false;
        }

        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            byte[] signature = Convert.FromBase64String(file.Signature);
            return key.VerifyData(Signed(file.Digest), signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private static byte[] Signed(string digest) => Encoding.UTF8.GetBytes(SignedPrefix + digest);
}
