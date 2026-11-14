using System.Net;
using System.Text.Json;

namespace AbpPlugin.Host.Tests;

/// <summary>
/// A plugin's web part as a browser gets it from a host on AbpPlugin.Web: its remote in the list, with what the host adds
/// to it; its files served from the version installed, every installed version's after an upgrade, none while it's
/// disabled, nothing outside its web part, each with its type and asked for again each time.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class WebPartTests(SamplePackages packages)
{
    private const string Remotes = "/api/plugins-web/remotes";
    private const string Prefix = $"/plugins-web/{SamplePackages.Key}/";

    [Fact]
    public async Task A_plugins_web_part_is_listed_and_served_while_it_is_enabled()
    {
        await using SampleHost host = new();
        await host.TrustAsync(packages.PublicKey);
        using HttpClient browser = host.AnonymousClient();

        Assert.Empty(await RemotesAsync(host));
        await host.InstalledAsync("/admin/plugins", packages.V010);

        // Listed for a signed-in person, with the host's own section; not for anybody else.
        JsonElement plugin = Assert.Single(await RemotesAsync(host));
        Assert.Equal((SamplePackages.Key, "0.1.0", SamplePackages.WebRemote, Prefix + "0.1.0/remoteEntry.js", "AbpPlugin sample"),
            (plugin.GetProperty("key").GetString(), plugin.GetProperty("version").GetString(), plugin.GetProperty("remote").GetProperty("name").GetString(),
                plugin.GetProperty("remote").GetProperty("entry").GetString(), plugin.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(Remotes)).StatusCode);

        // Its files, to anybody, as a page's scripts are.
        string entry = plugin.GetProperty("remote").GetProperty("entry").GetString()!;
        HttpResponseMessage script = await browser.GetAsync(entry);
        Assert.Equal((HttpStatusCode.OK, "text/javascript; charset=utf-8", "no-cache"),
            (script.StatusCode, script.Content.Headers.ContentType?.ToString(), script.Headers.CacheControl?.ToString()));
        Assert.Contains("0.1.0", await script.Content.ReadAsStringAsync());
        HttpResponseMessage json = await browser.GetAsync(Prefix + "0.1.0/mf-manifest.json");
        Assert.Equal((HttpStatusCode.OK, "application/json; charset=utf-8"), (json.StatusCode, json.Content.Headers.ContentType?.ToString()));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(Prefix + "0.1.0/assets/chunk.js")).StatusCode);

        // Nothing it hasn't got, nothing of another's, and nothing outside its web part.
        foreach (string missing in new[] { Prefix + "0.1.0/none.js", "/plugins-web/acme.none/0.1.0/remoteEntry.js", Prefix + "0.0.9/remoteEntry.js" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(missing)).StatusCode);
        }

        foreach (string escape in new[] { Prefix + "0.1.0/..%2Fplugin.json", Prefix + "0.1.0/%2E%2E/plugin.json", Prefix + "0.1.0/..%5Cplugin.json" })
        {
            HttpStatusCode status = (await browser.GetAsync(escape)).StatusCode;
            Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{escape} answered {status}");
        }

        // Upgraded: the list gives the new version; the old one's files are still served, for a page opened before.
        await host.InstalledAsync($"/admin/plugins/{SamplePackages.Key}/upgrade", packages.V020);
        JsonElement upgraded = Assert.Single(await RemotesAsync(host));
        string newEntry = upgraded.GetProperty("remote").GetProperty("entry").GetString()!;
        Assert.Equal(Prefix + "0.2.0/remoteEntry.js", newEntry);
        Assert.Contains("0.2.0", await browser.GetStringAsync(newEntry));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(entry)).StatusCode);

        // Disabled: gone from the list, and none of its files are served until it's enabled again.
        Assert.True((await host.Client.PostAsync($"/admin/plugins/{SamplePackages.Key}/disable", null)).IsSuccessStatusCode);
        Assert.Empty(await RemotesAsync(host));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(newEntry)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(entry)).StatusCode);
        Assert.True((await host.Client.PostAsync($"/admin/plugins/{SamplePackages.Key}/enable", null)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(newEntry)).StatusCode);
        Assert.Single(await RemotesAsync(host));
    }

    private static async Task<JsonElement[]> RemotesAsync(SampleHost host)
    {
        HttpResponseMessage response = await host.Client.GetAsync(Remotes);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{Remotes} answered {(int)response.StatusCode}: {text}");
        return [.. JsonDocument.Parse(text).RootElement.GetProperty("plugins").EnumerateArray().Select(e => e.Clone())];
    }
}
