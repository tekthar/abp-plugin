using AbpPlugin.Web;

namespace AbpPlugin.Tests;

/// <summary>A plugin's web file is never served from outside its part's folder, and each is served with its type.</summary>
public class PluginWebTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "plugins", "acme.reports", "1.0.0", "web");

    [Theory]
    [InlineData("remoteEntry.js")]
    [InlineData("assets/chunk-1.js")]
    [InlineData("assets/../remoteEntry.js")]
    public void A_file_inside_the_part_resolves(string file) =>
        Assert.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, PluginPartFiles.Resolve(Root, file));

    [Theory]
    [InlineData("../plugin.json")]
    [InlineData("../../0.9.0/web/remoteEntry.js")]
    [InlineData("")]
    [InlineData(".")]
    public void A_path_out_of_the_part_does_not(string file) => Assert.Null(PluginPartFiles.Resolve(Root, file));

    [Fact]
    public void A_backslash_leads_out_only_where_it_separates_folders()
    {
        string? resolved = PluginPartFiles.Resolve(Root, "..\\plugin.json");
        if (Path.DirectorySeparatorChar == '\\')
        {
            Assert.Null(resolved);
        }
        else
        {
            Assert.NotNull(resolved); // a file named "..\plugin.json" inside the part, which isn't there
        }
    }

    [Fact]
    public void An_absolute_path_does_not_resolve() =>
        Assert.Null(PluginPartFiles.Resolve(Root, Path.Combine(Path.GetTempPath(), "elsewhere.js")));

    [Theory]
    [InlineData("remoteEntry.js", "text/javascript; charset=utf-8")]
    [InlineData("index.bundle", "text/javascript; charset=utf-8")]
    [InlineData("mf-manifest.json", "application/json; charset=utf-8")]
    [InlineData("remoteEntry.js.map", "application/json")]
    [InlineData("style.css", "text/css; charset=utf-8")]
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("data.bin", "application/octet-stream")]
    public void Each_file_has_its_type(string file, string type) => Assert.Equal(type, PluginPartFiles.ContentTypeOf(file));
}
