using System.Net;
using System.Text.Json;
using AbpPlugin.Hosting;
using AbpPlugin.SampleHost;
using Microsoft.Extensions.DependencyInjection;

namespace AbpPlugin.Host.Tests;

/// <summary>
/// A plugin's whole life in a host that isn't the application it came from: the sample plugin, built
/// on AbpPlugin.Sdk alone and signed by its publisher, is installed, runs every part of the runtime, is upgraded with its
/// data kept, disabled and unloaded, enabled again, and removed with its schema.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class LifecycleTests(SamplePackages packages)
{
    private const string Ping = $"/api/plugins/{SamplePackages.Key}/ping";

    [Fact]
    public async Task A_plugin_is_installed_upgraded_unloaded_and_removed()
    {
        await using SampleHost host = new();
        await host.TrustAsync(packages.PublicKey);

        SampleAdmin.PluginRow installed = await host.InstalledAsync("/admin/plugins", packages.V010);
        Assert.Equal((SamplePackages.Key, "0.1.0", "Enabled", PluginSignatureKinds.Publisher, "Acme"),
            (installed.Key, installed.Version, installed.State, installed.SignatureKind, installed.Signer));
        string schema = PluginKeys.SchemaOf(SamplePackages.Key);
        Assert.True(await host.HasSchemaAsync(schema), $"its schema {schema} was made");

        // Its seeder ran, its endpoint answers a signed-in caller and nobody else.
        JsonElement first = await PingAsync(host);
        Assert.Equal((SamplePackages.Key, schema, 1, true), (first.GetProperty("key").GetString(), first.GetProperty("schema").GetString(),
            first.GetProperty("visits").GetInt32(), first.GetProperty("seeded").GetBoolean()));
        using (HttpClient anonymous = host.AnonymousClient())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Ping)).StatusCode);
        }

        // A visit: its table, its setting, its file, its secret, and its own event heard by its handler.
        HttpResponseMessage visit = await host.Client.PostAsync($"/api/plugins/{SamplePackages.Key}/visit?note=hello", null);
        Assert.True(visit.IsSuccessStatusCode, await visit.Content.ReadAsStringAsync());
        JsonElement after = await PingAsync(host, until: p => p.GetProperty("heard").GetInt32() == 1);
        Assert.Equal((2, "hello", "hello", "hello", 1, true), (after.GetProperty("visits").GetInt32(), after.GetProperty("last").GetString(),
            after.GetProperty("file").GetString(), after.GetProperty("secret").GetString(), after.GetProperty("heard").GetInt32(), after.GetProperty("caller").GetBoolean()));

        // Upgraded: the new version runs on the same data; the seeder adds no second first visit.
        SampleAdmin.PluginRow upgraded = await host.InstalledAsync($"/admin/plugins/{SamplePackages.Key}/upgrade", packages.V020);
        Assert.Equal(("0.2.0", "Enabled"), (upgraded.Version, upgraded.State));
        JsonElement onUpgrade = await PingAsync(host);
        Assert.Equal((2, "hello", 1), (onUpgrade.GetProperty("visits").GetInt32(), onUpgrade.GetProperty("last").GetString(), onUpgrade.GetProperty("heard").GetInt32()));
        Assert.Contains(host.Services.GetRequiredService<PluginRuntime>().GetUnloadReport(), r => r.Key == SamplePackages.Key && r.Version == "0.1.0" && r.Collected);

        // Disabled: its endpoints are gone and its code unloads; enabled again, it has its data.
        HttpResponseMessage disable = await host.Client.PostAsync($"/admin/plugins/{SamplePackages.Key}/disable", null);
        Assert.True(disable.IsSuccessStatusCode, await disable.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(Ping)).StatusCode);
        Assert.Contains(host.Services.GetRequiredService<PluginRuntime>().GetUnloadReport(), r => r.Key == SamplePackages.Key && r.Version == "0.2.0" && r.Collected);
        HttpResponseMessage enable = await host.Client.PostAsync($"/admin/plugins/{SamplePackages.Key}/enable", null);
        Assert.True(enable.IsSuccessStatusCode, await enable.Content.ReadAsStringAsync());
        Assert.Equal(2, (await PingAsync(host)).GetProperty("visits").GetInt32());

        // Removed: gone from the list and its endpoints too. Its data stays for the retention period (a purge is a step of
        // its own), so installing it again brings its visits back.
        HttpResponseMessage remove = await host.Client.DeleteAsync($"/admin/plugins/{SamplePackages.Key}");
        Assert.True(remove.IsSuccessStatusCode, await remove.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(Ping)).StatusCode);
        Assert.DoesNotContain(SamplePackages.Key, await host.Client.GetStringAsync("/admin/plugins"));
        Assert.Contains(host.Services.GetRequiredService<PluginRuntime>().GetUnloadReport(), r => r.Key == SamplePackages.Key && r.Version == "0.2.0" && r.Collected);
        Assert.True(await host.HasSchemaAsync(schema), $"its schema {schema} is kept");
        SampleAdmin.PluginRow again = await host.InstalledAsync("/admin/plugins", packages.V020);
        Assert.Equal(("0.2.0", "Enabled"), (again.Version, again.State));
        Assert.Equal(2, (await PingAsync(host)).GetProperty("visits").GetInt32());
    }

    [Fact]
    public async Task A_package_nobody_trusts_waits_for_the_admins_word()
    {
        await using SampleHost host = new();

        string unsigned = await host.RefusedAsync("/admin/plugins", packages.Unsigned);
        Assert.Contains("unsigned", unsigned, StringComparison.OrdinalIgnoreCase);
        string stranger = await host.RefusedAsync("/admin/plugins", packages.Stranger);
        Assert.Contains("Someone else", stranger);

        HttpResponseMessage allow = await host.Client.PutAsync("/admin/unsigned", JsonContent("""{ "allow": true }"""));
        Assert.True(allow.IsSuccessStatusCode, await allow.Content.ReadAsStringAsync());
        SampleAdmin.PluginRow installed = await host.InstalledAsync("/admin/plugins", packages.Unsigned);
        Assert.Equal((PluginSignatureKinds.Unsigned, "Enabled"), (installed.SignatureKind, installed.State));
    }

    [Fact]
    public async Task A_package_changed_after_signing_is_refused()
    {
        await using SampleHost host = new();
        await host.TrustAsync(packages.PublicKey);

        string changed = await host.RefusedAsync("/admin/plugins", packages.Changed);
        Assert.Contains("changed", changed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("[]", await host.Client.GetStringAsync("/admin/plugins"));
    }

    private static StringContent JsonContent(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    /// <summary>The sample's ping, as JSON; with <paramref name="until"/>, asked again for up to five seconds until it holds.</summary>
    private static async Task<JsonElement> PingAsync(SampleHost host, Func<JsonElement, bool>? until = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            HttpResponseMessage response = await host.Client.GetAsync(Ping);
            string text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"ping answered {(int)response.StatusCode}: {text}");
            JsonElement ping = JsonDocument.Parse(text).RootElement.Clone();
            if (until is null || until(ping) || attempt == 50)
            {
                return ping;
            }

            await Task.Delay(100);
        }
    }
}
