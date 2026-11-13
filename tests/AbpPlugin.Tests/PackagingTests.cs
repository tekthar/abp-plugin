using AbpPlugin.Packaging;

namespace AbpPlugin.Tests;

/// <summary>
/// AbpPlugin.Packaging: the package format a host, a build, a signing tool and a marketplace share. Only .NET's own
/// assemblies, AbpPlugin's own names by default, an application's names when it says so, and signatures that break on
/// any change.
/// </summary>
public sealed class PackagingTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("abp-plugin-packaging-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_format_uses_only_dotnets_own_assemblies()
    {
        string[] references = [.. typeof(PluginSignature).Assembly.GetReferencedAssemblies().Select(a => a.Name!)];
        Assert.All(references, name => Assert.True(name == "System" || name.StartsWith("System.", StringComparison.Ordinal) || name == "netstandard", name));
    }

    [Fact]
    public void AbpPlugins_own_names_are_the_default()
    {
        Assert.Same(PluginPackageFormat.Default, PluginPackageFormat.Current);
        Assert.Equal(("plugin.json", "plugin-signature.json", ".plugin-package", "abp-plugin-signature/1\n"),
            (PluginPackageFormat.Default.Manifest, PluginPackageFormat.Default.Signature, PluginPackageFormat.Default.Marker, PluginPackageFormat.Default.SignedPrefix));
        Assert.Equal([".zip"], PluginPackageFormat.Default.PackageExtensions);
        Assert.Equal(PluginPackageFormat.Default.Signature, PluginSignature.FileName);
    }

    [Fact]
    public void An_applications_format_finds_a_manifest_by_a_former_name()
    {
        PluginPackageFormat format = new() { Manifest = "app-plugin.json", FormerManifests = ["old-plugin.json"] };
        Assert.Null(format.ManifestIn(_folder));
        File.WriteAllText(Path.Combine(_folder, "old-plugin.json"), "{}");
        Assert.Equal(Path.Combine(_folder, "old-plugin.json"), format.ManifestIn(_folder));
        File.WriteAllText(Path.Combine(_folder, "app-plugin.json"), "{}");
        Assert.Equal(Path.Combine(_folder, "app-plugin.json"), format.ManifestIn(_folder));
    }

    [Fact]
    public void A_manifest_says_its_key_version_and_requirements()
    {
        PluginPackageManifest manifest = PluginPackageManifest.Parse("""
            // a comment, as a template may have
            { "key": "acme.ratings", "name": "Ratings", "version": "1.4", "requires": { "host": ">=0.1.0 <2.0.0", "other": 3 }, }
            """);

        Assert.Equal("acme.ratings", manifest.Key);
        Assert.Equal(new SemanticVersion(1, 4, 0), manifest.Version);
        Assert.Equal(">=0.1.0 <2.0.0", manifest.Requires["host"]);
        Assert.False(manifest.Requires.ContainsKey("other"));
        Assert.True(manifest.Version.Satisfies(manifest.Requires["host"]));
    }

    [Theory]
    [InlineData("""{ "version": "1.0.0" }""", "has no key")]
    [InlineData("""{ "key": "acme.x" }""", "has no version")]
    [InlineData("""{ "key": "acme.x", "version": "one" }""", "isn't one to three numbers")]
    [InlineData("""[1, 2]""", "must be a JSON object")]
    [InlineData("""{ "key": """, "isn't valid JSON")]
    public void A_manifest_without_what_it_needs_is_refused_saying_which(string json, string says)
    {
        PluginException refused = Assert.ThrowsAny<PluginException>(() => PluginPackageManifest.Parse(json));
        Assert.Contains(says, refused.Message);
    }

    [Theory]
    [InlineData("1.2.3", ">=1.0.0 <2.0.0", true)]
    [InlineData("2.0.0", ">=1.0.0 <2.0.0", false)]
    [InlineData("1.2", "=1.2.0", true)]
    [InlineData("0.9.9", ">0.9.8", true)]
    [InlineData("0.9.8", ">0.9.8", false)]
    [InlineData("3.1.0-beta.1", "<=3.1.0", true)]
    public void A_version_satisfies_a_range_of_comparators(string version, string range, bool satisfies) =>
        Assert.Equal(satisfies, SemanticVersion.Parse(version).Satisfies(range));

    [Fact]
    public void A_signed_package_verifies_with_its_key_and_no_other()
    {
        File.WriteAllText(Path.Combine(_folder, PluginPackageFormat.Current.Manifest), """{ "key": "acme.sample", "version": "1.0.0" }""");
        Directory.CreateDirectory(Path.Combine(_folder, "web"));
        File.WriteAllText(Path.Combine(_folder, "web", "remoteEntry.js"), "export {};");
        (string privatePem, string publicPem, string keyId) = PluginSignature.CreateKey();

        PluginSignatureFile signed = PluginSignature.Sign(_folder, privatePem, "Acme");
        Assert.Equal((keyId, "Acme", PluginSignature.Algorithm), (signed.KeyId, signed.Signer, signed.Algorithm));
        PluginSignatureFile read = PluginSignature.Read(_folder)!;
        byte[] key = PluginSignature.ReadPublicKey(publicPem)!;
        Assert.True(PluginSignature.Matches(read, _folder));
        Assert.True(PluginSignature.Verify(read, _folder, key));
        Assert.Equal(keyId, PluginSignature.KeyId(key));

        byte[] other = PluginSignature.ReadPublicKey(PluginSignature.CreateKey().PublicPem)!;
        Assert.False(PluginSignature.Verify(read, _folder, other));
        Assert.True(PluginSignature.IsPrivateKey(privatePem));
        Assert.Null(PluginSignature.ReadPublicKey(privatePem));
    }

    [Fact]
    public void A_formats_unsigned_files_and_former_marks_are_left_out_of_the_digest()
    {
        PluginPackageFormat before = PluginPackageFormat.Current;
        PluginPackageFormat.Current = new() { FormerMarkers = [".old-package"], Unsigned = ["countersignature.json"] };
        try
        {
            File.WriteAllText(Path.Combine(_folder, PluginPackageFormat.Current.Manifest), """{ "key": "acme.sample", "version": "1.0.0" }""");
            (string privatePem, string publicPem, _) = PluginSignature.CreateKey();
            PluginSignature.Sign(_folder, privatePem, "Acme");
            byte[] key = PluginSignature.ReadPublicKey(publicPem)!;

            File.WriteAllText(Path.Combine(_folder, "countersignature.json"), "{ }");
            File.WriteAllText(Path.Combine(_folder, ".old-package"), "hash");
            PluginSignatureFile read = PluginSignature.Read(_folder)!;
            Assert.True(PluginSignature.Matches(read, _folder));
            Assert.True(PluginSignature.Verify(read, _folder, key));

            File.WriteAllText(Path.Combine(_folder, "other.json"), "{ }");
            Assert.False(PluginSignature.Matches(read, _folder));
        }
        finally
        {
            PluginPackageFormat.Current = before;
        }
    }

    [Fact]
    public void A_file_changed_added_or_removed_after_signing_breaks_the_signature()
    {
        File.WriteAllText(Path.Combine(_folder, PluginPackageFormat.Current.Manifest), """{ "key": "acme.sample", "version": "1.0.0" }""");
        (string privatePem, string publicPem, _) = PluginSignature.CreateKey();
        PluginSignature.Sign(_folder, privatePem, "Acme");
        byte[] key = PluginSignature.ReadPublicKey(publicPem)!;
        PluginSignatureFile read = PluginSignature.Read(_folder)!;

        // The host's own mark on an unpacked package isn't the publisher's: the digest leaves it out.
        File.WriteAllText(Path.Combine(_folder, PluginPackageFormat.Current.Marker), "hash");
        Assert.True(PluginSignature.Verify(read, _folder, key));

        File.WriteAllText(Path.Combine(_folder, "extra.dll"), "added");
        Assert.False(PluginSignature.Matches(read, _folder));
        File.Delete(Path.Combine(_folder, "extra.dll"));
        Assert.True(PluginSignature.Matches(read, _folder));
        File.AppendAllText(Path.Combine(_folder, PluginPackageFormat.Current.Manifest), " ");
        Assert.False(PluginSignature.Verify(read, _folder, key));
    }
}
