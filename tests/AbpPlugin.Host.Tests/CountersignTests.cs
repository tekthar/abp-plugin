using AbpPlugin.Hosting;
using AbpPlugin.SampleHost;

namespace AbpPlugin.Host.Tests;

/// <summary>
/// Packages a countersigning authority (a marketplace) countersigned, in a host that trusts its root: they go in as the
/// certified publisher's without the publisher's key added by hand, an upgrade takes the same publisher's new key, and
/// another publisher's package or one countersigned for another version is refused. Without the root they're as any
/// package signed by a key the site doesn't know.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class CountersignTests(SamplePackages packages)
{
    [Fact]
    public async Task A_countersigned_package_goes_in_as_its_certified_publishers_and_takes_their_new_key()
    {
        await using SampleHost host = new(packages.RootKey);

        SampleAdmin.PluginRow installed = await host.InstalledAsync("/admin/plugins", packages.Countersigned010);
        Assert.Equal(("0.1.0", "Enabled", PluginSignatureKinds.Marketplace, "acme"), (installed.Version, installed.State, installed.SignatureKind, installed.Signer));

        SampleAdmin.PluginRow upgraded = await host.InstalledAsync($"/admin/plugins/{SamplePackages.Key}/upgrade", packages.NewKey020);
        Assert.Equal(("0.2.0", "Enabled", PluginSignatureKinds.Marketplace, "acme"), (upgraded.Version, upgraded.State, upgraded.SignatureKind, upgraded.Signer));
    }

    [Fact]
    public async Task Another_publishers_package_and_one_countersigned_for_another_version_are_refused()
    {
        await using SampleHost host = new(packages.RootKey);
        await host.InstalledAsync("/admin/plugins", packages.Countersigned010);

        string other = await host.RefusedAsync($"/admin/plugins/{SamplePackages.Key}/upgrade", packages.OtherPublisher020);
        Assert.Contains("for the publisher 'beta'", other);
        string wrong = await host.RefusedAsync($"/admin/plugins/{SamplePackages.Key}/upgrade", packages.WrongVersion020);
        Assert.Contains($"made for {SamplePackages.Key} 0.1.0", wrong);

        // A version signed only by a key the admins trusted by hand isn't the certified publisher's either.
        await host.TrustAsync(packages.PublicKey);
        SampleAdmin.PluginRow same = await host.InstalledAsync($"/admin/plugins/{SamplePackages.Key}/upgrade", packages.V020);
        Assert.Equal(("0.2.0", PluginSignatureKinds.Publisher), (same.Version, same.SignatureKind)); // the same key Acme signed 0.1.0 with
    }

    [Fact]
    public async Task Without_the_root_a_countersigned_package_is_signed_by_a_key_the_site_doesnt_know()
    {
        await using SampleHost host = new();

        string refused = await host.RefusedAsync("/admin/plugins", packages.Countersigned010);
        Assert.Contains("a key this site doesn't trust", refused);

        await host.TrustAsync(packages.PublicKey);
        SampleAdmin.PluginRow installed = await host.InstalledAsync("/admin/plugins", packages.Countersigned010);
        Assert.Equal(PluginSignatureKinds.Publisher, installed.SignatureKind);
    }
}
