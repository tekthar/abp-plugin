# AbpPlugin

A plugin host for ABP applications on .NET 10, EF Core and PostgreSQL. It installs signed plugin packages while the
application runs, each in an unloadable load context with its own database schema, and upgrades, disables, enables
and removes them without a restart.

> **Preview.** 0.x versions may change their API in any minor version (SemVer). Pin an exact version.

> **Not a Volosoft project.** [ABP Framework](https://abp.io) is made and owned by [Volosoft](https://volosoft.com).
> AbpPlugin is an independent enhancement that works on ABP: this repository and its packages don't belong to
> Volosoft, and aren't affiliated with or endorsed by them.

## How it differs from ABP's plug-in modules

ABP's own [plug-in modules](https://abp.io/docs/latest/framework/architecture/modularity/plugin-modules) load module
assemblies from a folder (`PlugInSources`) when the application starts. They become modules of the application, stay
loaded until it stops, and change only with a restart.

AbpPlugin installs signed packages at run time. Each plugin version loads into a collectible `AssemblyLoadContext`
with a DI container and a database schema of its own, and is upgraded, disabled (its code unloaded), enabled again and
removed while the application keeps running. A plugin isn't an ABP module: it says what it adds through
`IPluginServices`, and gets from the host only what the host exposes.

## What it does

- **Install at run time.** `PluginManager.InstallAsync` takes a plugin's folder or zip, checks it, keeps the package
  in blob storage, migrates the plugin's tables, runs its seeders and starts it.
- **Each plugin apart.** A plugin shares the host's assemblies (ABP, ASP.NET Core, EF Core, Npgsql, the SDK) and loads
  the rest privately. Its tables are in a schema of its own (`plg_acme__ratings` for `acme.ratings`), and its context
  can't map a table anywhere else. It reads only its own settings, files, secrets and extension points.
- **Upgrade, disable, enable, remove.** An upgrade loads the new version beside the old one, migrates, and swaps it in
  one step: calls in flight finish on the old version. Disabling stops new calls, waits for running ones and unloads
  the code; `PluginRuntime.GetUnloadReport` shows whether it was collected. Removing a plugin keeps its schema and data.
- **Signed packages.** ECDSA P-256 over a digest of every file. A site trusts release keys (from its configuration, or
  built into the application) and publishers' keys its admins add. A package whose files changed after signing never
  goes in, and an upgrade must come from the plugin's own signer. Unsigned packages go in only where the server's
  operator lets admins allow them, and the admins do.
- **What a plugin can do** with `AbpPlugin.Sdk` alone: an EF Core context with migrations, seeders, HTTP endpoints for
  signed-in callers, public endpoints for webhooks (capped and rate limited), local and distributed event handlers,
  cron jobs, implementations of the host's extension points, health checks, settings by key, files in blob storage,
  secrets under Data Protection, and contributions from other plugins' manifests to its own extension points.
- **One unit of work.** A call that may change something (an endpoint other than GET and HEAD, a job, an event handler)
  runs in one transaction with the server's: what the plugin saves and what the server's services it calls change
  commit together, or not at all.
- **Several servers.** Each server brings its plugins in line with the plugins table every 15 seconds. Packages come
  from blob storage, checked against the hash recorded at install. One server at a time migrates a plugin, and each
  occurrence of a recurring job runs once for the site.
- **Seams with defaults for one server:** who the caller is, the public endpoints' limits, the application's own checks
  on a plugin coming in, notices that plugins changed, and recurring jobs' slots. An application with several servers,
  or rules of its own, registers its own.

## Packages

| Package | Holds | Depends on |
|---|---|---|
| `AbpPlugin` | The host: `AbpPluginModule`, `PluginManager`, `PluginTrust`, `PluginRuntime`, the endpoint dispatcher, the seams and their defaults, the table mapping | ABP 10.6 (ASP.NET Core, EF Core on PostgreSQL, blob storing), AbpCluster, Cronos |
| `AbpPlugin.Sdk` | What a plugin references: `PluginEntry`, `IPluginServices`, `PluginDbContext`, endpoints, events, jobs, settings, files, secrets, health checks, seeders, contributions | ASP.NET Core, EF Core (relational) |
| `AbpPlugin.Packaging` | The package format: `PluginPackageFormat`, `PluginSignature`, `PluginSignatureFile`, `PluginPackageManifest`, `SemanticVersion`, `PluginException`, and the signing tool's commands (`PluginSigningTool`). A marketplace or a build tool can use it | Plain .NET |
| `AbpPlugin.Build` | MSBuild targets that pack a plugin after its build, and sign it when given a key | Nothing (targets only) |
| `AbpPlugin.Sign` | A .NET tool, `abp-plugin-sign`: `keygen`, `sign`, `verify`, `keyid` | `AbpPlugin.Packaging` |

## Getting started

Host plugins in an ABP application:

```csharp
[DependsOn(typeof(AbpAutofacModule), typeof(AbpBlobStoringFileSystemModule), typeof(AbpPluginModule))]
public class MyHostModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<MyDbContext>(options => options.ReplaceDbContext<IPluginsDbContext>());
        Configure<AbpDbContextOptions>(options => options.UseNpgsql());
        Configure<AbpBlobStoringOptions>(options => options.Containers.ConfigureDefault(container =>
            container.UseFileSystem(fileSystem => fileSystem.BasePath = "/var/lib/my-app/blobs")));
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        IApplicationBuilder app = context.GetApplicationBuilder();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints => endpoints.MapPluginEndpoints());   // /api/plugins/{key}/...
    }
}

// MyDbContext implements IPluginsDbContext; in its OnModelCreating:
PluginsModel.ConfigurePlugins(modelBuilder);

// In Program.cs, once the application is initialized and its migrations have run:
await app.Services.GetRequiredService<PluginManager>().StartAsync();
```

Write a plugin on `AbpPlugin.Sdk`, and pack it with `AbpPlugin.Build`:

```csharp
public sealed class HelloPlugin : PluginEntry
{
    public override void ConfigureServices(IPluginServices services) => services
        .AddEndpoint<Hello>("GET", "hello");
}

public sealed class Hello(PluginContext context, IPluginJson json) : IPluginEndpoint
{
    public Task<IResult> HandleAsync(PluginRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(json.Result(new { context.Key, context.Version }));
}
```

Then install its zip with `PluginManager.InstallAsync(path)`, from an admin page of your own.

See [hosting](docs/hosting.md), [building plugins](docs/building-plugins.md), [the package format](docs/package-format.md),
and [moving an application onto AbpPlugin](docs/moving-onto-abp-plugin.md). Why it is shaped this way is in
[ADR 0001](docs/adr/0001-abp-plugin-as-a-package.md). `samples/AbpPlugin.SampleHost` is a complete host, and
`samples/AbpPlugin.Sample` a plugin that uses every part of the SDK.

## Building

```bash
dotnet build AbpPlugin.slnx
```

The host tests build the sample plugin at two versions, sign it, and run its whole life on the sample host. They need
PostgreSQL on localhost:5432 (postgres/postgres), or the server `ABP_PLUGIN_TEST_POSTGRES` names, as a user that may
create and drop databases.

```bash
dotnet test AbpPlugin.slnx
bash scripts/check-names.sh     # no product's name but AbpPlugin's
bash scripts/check-release.sh   # the version has its CHANGELOG.md entry
```

## Releasing

Set `VersionPrefix` and `VersionSuffix` in `Directory.Build.props`, move CHANGELOG.md's Unreleased to the version, and
push a tag `v<version>` (`v0.1.0`). CI runs the checks, then pushes `AbpPlugin`, `AbpPlugin.Sdk`,
`AbpPlugin.Packaging`, `AbpPlugin.Build` and `AbpPlugin.Sign` to nuget.org through Trusted Publishing (the policy
names this repository, `ci.yml` and the `release` environment; the repository variable `NUGET_USER` is the nuget.org
account) and makes a GitHub release.

## License

Apache 2.0. See [LICENSE](LICENSE).
