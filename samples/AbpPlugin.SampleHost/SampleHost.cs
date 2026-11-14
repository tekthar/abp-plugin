using System.Security.Claims;
using System.Text.Encodings.Web;
using AbpPlugin.Hosting;
using AbpPlugin.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.Autofac;
using Volo.Abp.BlobStoring;
using Volo.Abp.BlobStoring.FileSystem;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;
using Volo.Abp.Security.Claims;

namespace AbpPlugin.SampleHost;

/// <summary>
/// The plugin host's module and ABP's: all a host needs. The sample's context holds the host's tables; packages and
/// plugins' files are on disk under its data folder (<c>Sample:DataRoot</c>); a request names its account in a header,
/// standing in for an application's own sign-in; the host's defaults do the rest. Plugins' web parts are served at
/// /plugins-web/, and their remotes listed for a signed-in person (AbpPlugin.Web).
/// </summary>
[DependsOn(typeof(AbpAutofacModule), typeof(AbpBlobStoringFileSystemModule), typeof(AbpPluginModule), typeof(AbpPluginWebModule))]
public class SampleHostModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        string data = SampleData.Root(context.Services.GetConfiguration());
        context.Services.AddAbpDbContext<SampleDbContext>(options => options.ReplaceDbContext<IPluginsDbContext>());
        Configure<AbpDbContextOptions>(options => options.UseNpgsql());
        Configure<AbpBlobStoringOptions>(options => options.Containers.ConfigureDefault(container =>
            container.UseFileSystem(fileSystem => fileSystem.BasePath = Path.Combine(data, "blobs"))));
        Configure<PluginHostOptions>(options => options.PluginsRoot = Path.Combine(data, "plugins"));
        context.Services.AddAuthentication(SampleAccount.Scheme).AddScheme<AuthenticationSchemeOptions, SampleAccount>(SampleAccount.Scheme, null);
        context.Services.AddAuthorization();
        context.Services.AddSingleton<IPluginWebSections, SampleWebSections>();
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        IApplicationBuilder app = context.GetApplicationBuilder();
        // The page (web/, built into wwwroot): the enabled plugins' pages, loaded in the browser.
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapPluginEndpoints();
            endpoints.MapPluginWeb().RequireAuthorization();
            endpoints.MapSampleAdmin();
            endpoints.MapGet("/sample/sign-in", (HttpContext http) =>
            {
                http.Response.Cookies.Append(SampleAccount.Cookie, SampleAccount.Admin.ToString(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict });
                return Results.Redirect("/");
            });
        });
    }
}

/// <summary>The sample's data folder: <c>Sample:DataRoot</c>, or App_Data beside the application.</summary>
public static class SampleData
{
    public static string Root(IConfiguration configuration) =>
        configuration["Sample:DataRoot"] is { Length: > 0 } root ? root : Path.Combine(AppContext.BaseDirectory, "App_Data");
}

/// <summary>The sample's context: the plugin host's tables, in schema <c>plugins</c>.</summary>
[ConnectionStringName("Default")]
public class SampleDbContext(DbContextOptions<SampleDbContext> options) : AbpDbContext<SampleDbContext>(options), IPluginsDbContext
{
    public DbSet<PluginInstallation> Plugins => Set<PluginInstallation>();
    public DbSet<PluginPublisher> PluginPublishers => Set<PluginPublisher>();
    public DbSet<PluginTrustSettings> PluginTrustSettings => Set<PluginTrustSettings>();
    public DbSet<PluginFileRecord> PluginFiles => Set<PluginFileRecord>();
    public DbSet<PluginManifestRecord> PluginManifests => Set<PluginManifestRecord>();
    public DbSet<PluginSettingRecord> PluginSettings => Set<PluginSettingRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("plugins");
        PluginsModel.ConfigurePlugins(modelBuilder);
    }
}

/// <summary>For <c>dotnet ef migrations add</c> only: the sample's migrations are made from its context, as a host's are.</summary>
public class SampleDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<SampleDbContext>
{
    public SampleDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SampleDbContext>().UseNpgsql("Host=localhost;Port=5432;Database=abp_plugin_sample;Username=postgres;Password=postgres").Options);
}

/// <summary>
/// The sample's sign-in: a request names its account in <c>X-Sample-Account</c> (a Guid), or the page's cookie does
/// (<c>/sample/sign-in</c> sets the admin's). It stands in for the application's own sign-in, so a plugin's endpoints, which need a signed-in caller, have one; never use it in an
/// application people reach.
/// </summary>
public sealed class SampleAccount(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "SampleAccount";
    public const string Header = "X-Sample-Account";
    public const string Cookie = "sample-account";

    /// <summary>The account /sample/sign-in signs the page in as.</summary>
    public static readonly Guid Admin = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? value = Request.Headers.TryGetValue(Header, out var header) ? header.ToString() : Request.Cookies[Cookie];
        if (!Guid.TryParse(value, out Guid account))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        ClaimsIdentity identity = new([new Claim(AbpClaimTypes.UserId, account.ToString())], Scheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}
