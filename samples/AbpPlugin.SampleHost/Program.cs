using AbpPlugin.Hosting;
using AbpPlugin.SampleHost;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Uow;

// A host of AbpPlugin alone: plugins installed at run time, their tables in the host's database, their files on disk.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Host.UseAutofac();
await builder.AddApplicationAsync<SampleHostModule>();

WebApplication app = builder.Build();
await app.InitializeApplicationAsync();
await MigrateAsync(app.Services);
// The plugins installed before start load now; system plugins in PluginHostOptions.SystemPluginFolders install first.
await app.Services.GetRequiredService<PluginManager>().StartAsync();
await app.RunAsync();

// The host's tables come from the application's own migrations (Migrations/, dotnet ef migrations add after
// PluginsModel.ConfigurePlugins), run on start or in its deploy, as an application does for its own tables.
static async Task MigrateAsync(IServiceProvider services)
{
    using IServiceScope scope = services.CreateScope();
    using IUnitOfWork uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin();
    SampleDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextProvider<SampleDbContext>>().GetDbContextAsync();
    await db.Database.MigrateAsync();
    await uow.CompleteAsync();
}

/// <summary>The program, for the tests' WebApplicationFactory.</summary>
public partial class Program;
