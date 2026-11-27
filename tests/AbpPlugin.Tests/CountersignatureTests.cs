using System.Text.Json;
using System.Text.Json.Nodes;
using AbpPlugin.Packaging;

namespace AbpPlugin.Tests;

/// <summary>
/// A countersignature's chain, as a site checks it: it holds when a trusted root countersigned the publisher's signature
/// with its certificate for the publisher's key, and breaks with another root, a certificate out of date or from
/// another authority, a signature by another key, or files changed since.
/// </summary>
public sealed class CountersignatureTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("abp-plugin-countersign-").FullName;
    private readonly (string Private, string Public, string KeyId) _root = PluginSignature.CreateKey();
    private readonly (string Private, string Public, string KeyId) _publisher = PluginSignature.CreateKey();

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private byte[] Root => PluginSignature.ReadPublicKey(_root.Public)!;

    private string Package(string name = "package")
    {
        string folder = Path.Combine(_work, name);
        Directory.CreateDirectory(Path.Combine(folder, "lib"));
        File.WriteAllText(Path.Combine(folder, PluginPackageFormat.Current.Manifest), """{ "key": "acme.sample", "version": "1.0.0" }""");
        File.WriteAllText(Path.Combine(folder, "lib", "Acme.Sample.dll"), "code");
        PluginSignature.Sign(folder, _publisher.Private, "Acme");
        return folder;
    }

    private PluginKeyCertificate Certificate(string? issuerPrivate = null, DateTimeOffset? notAfter = null, string? publicPem = null) =>
        PluginCountersignature.Certify(issuerPrivate ?? _root.Private, "acme", "Acme", PluginSignature.ReadPublicKey(publicPem ?? _publisher.Public)!,
            DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddYears(1));

    private PluginCountersignatureCheck Check(string folder, params byte[][] roots) =>
        PluginCountersignature.Check(folder, PluginSignature.Read(folder), roots.Length > 0 ? roots : [Root]);

    [Fact]
    public void A_countersignature_by_a_trusted_root_holds_and_names_the_certified_publisher()
    {
        string folder = Package();
        PluginCountersignature.Countersign(folder, _root.Private, "The marketplace", "acme.sample", "1.0.0", Certificate());

        PluginCountersignatureCheck check = Check(folder);
        Assert.Equal(PluginCountersignatureState.Holds, check.State);
        Assert.Equal(("acme", "acme.sample", "1.0.0", _publisher.KeyId),
            (check.File!.Certificate.Publisher, check.File.Listing, check.File.ListingVersion, check.File.PublisherKeyId));

        // The countersignature is left out of the digest, so the publisher's signature still holds beside it.
        Assert.True(PluginSignature.Verify(PluginSignature.Read(folder)!, folder, PluginSignature.ReadPublicKey(_publisher.Public)!));
        Assert.Equal(PluginCountersignatureState.None, PluginCountersignature.Check(Package("plain"), null, [Root]).State);
    }

    [Fact]
    public void Another_root_is_not_trusted_and_a_changed_countersignature_is_broken()
    {
        string folder = Package();
        PluginCountersignature.Countersign(folder, _root.Private, "The marketplace", "acme.sample", "1.0.0", Certificate());
        Assert.Equal(PluginCountersignatureState.NotTrusted, Check(folder, PluginSignature.ReadPublicKey(PluginSignature.CreateKey().PublicPem)!).State);

        string path = Path.Combine(folder, PluginCountersignature.FileName);
        JsonObject file = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        file["listingVersion"] = "9.9.9";
        File.WriteAllText(path, file.ToJsonString());
        PluginCountersignatureCheck changed = Check(folder);
        Assert.Equal(PluginCountersignatureState.Broken, changed.State);
        Assert.Contains("changed, or made for something else", changed.Problem);

        File.WriteAllText(path, "not json");
        Assert.Equal(PluginCountersignatureState.Broken, Check(folder).State);
    }

    [Fact]
    public void A_certificate_out_of_date_or_from_another_authority_breaks_the_chain()
    {
        string expired = Package("expired");
        PluginCountersignature.Countersign(expired, _root.Private, "The marketplace", "acme.sample", "1.0.0", Certificate(notAfter: DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Contains("wasn't valid when it was countersigned", Check(expired).Problem);

        string stranger = Package("stranger");
        PluginCountersignature.Countersign(stranger, _root.Private, "The marketplace", "acme.sample", "1.0.0", Certificate(issuerPrivate: PluginSignature.CreateKey().PrivatePem));
        Assert.Contains("isn't one an authority this site trusts made", Check(stranger).Problem);
    }

    [Fact]
    public void Another_key_or_changed_files_break_the_chain()
    {
        string resigned = Package("resigned");
        PluginCountersignature.Countersign(resigned, _root.Private, "The marketplace", "acme.sample", "1.0.0", Certificate());
        PluginSignature.Sign(resigned, PluginSignature.CreateKey().PrivatePem, "Acme");
        Assert.Contains("another signature", Check(resigned).Problem);

        string changed = Package("changed");
        PluginCountersignature.Countersign(changed, _root.Private, "The marketplace", "acme.sample", "1.0.0", Certificate());
        File.AppendAllText(Path.Combine(changed, "lib", "Acme.Sample.dll"), "more");
        Assert.Contains("aren't the ones that were countersigned", Check(changed).Problem);

        // The authority won't countersign a signature that isn't its certified key's.
        Assert.Throws<InvalidOperationException>(() =>
            PluginCountersignature.Countersign(Package("uncertified"), _root.Private, "The marketplace", "acme.sample", "1.0.0",
                Certificate(publicPem: PluginSignature.CreateKey().PublicPem)));
    }

    [Fact]
    public void What_is_signed_starts_with_the_formats_labels()
    {
        PluginKeyCertificate certificate = Certificate();
        Assert.StartsWith(PluginPackageFormat.Current.CertificatePrefix, System.Text.Encoding.UTF8.GetString(PluginCountersignature.Signed(certificate)));
        string folder = Package();
        PluginCountersignatureFile file = PluginCountersignature.Countersign(folder, _root.Private, "The marketplace", "acme.sample", "1.0.0", certificate);
        Assert.StartsWith(PluginPackageFormat.Current.CountersignaturePrefix, System.Text.Encoding.UTF8.GetString(PluginCountersignature.Signed(file)));
        Assert.Equal(file, JsonSerializer.Deserialize<PluginCountersignatureFile>(File.ReadAllText(Path.Combine(folder, PluginCountersignature.FileName)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) with { Certificate = file.Certificate });
    }
}
