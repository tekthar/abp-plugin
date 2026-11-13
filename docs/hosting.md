# Hosting

How an ABP application hosts plugins: the module, the tables, starting, the endpoints, storage, settings, which
packages go in, administration, the seams, and what an application wires up itself. `samples/AbpPlugin.SampleHost` is
a complete host to read beside this.

## The module and the tables

Depend on `AbpPluginModule`, map the host's tables in your context, and say your context replaces the host's. The host
keeps packages and plugins' files in ABP blob storing, so configure a provider too. From the sample host:

```csharp
using AbpPlugin;
using AbpPlugin.Hosting;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.Autofac;
using Volo.Abp.BlobStoring;
using Volo.Abp.BlobStoring.FileSystem;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;

[DependsOn(typeof(AbpAutofacModule), typeof(AbpBlobStoringFileSystemModule), typeof(AbpPluginModule))]
public class MyHostModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<MyDbContext>(options => options.ReplaceDbContext<IPluginsDbContext>());
        Configure<AbpDbContextOptions>(options => options.UseNpgsql());
        Configure<AbpBlobStoringOptions>(options => options.Containers.ConfigureDefault(container =>
            container.UseFileSystem(fileSystem => fileSystem.BasePath = "/var/lib/my-app/blobs")));
        Configure<PluginHostOptions>(options => options.PluginsRoot = "/var/lib/my-app/plugins");
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        IApplicationBuilder app = context.GetApplicationBuilder();
        app.UseRouting();
        app.UseAuthentication();   // your application's sign-in
        app.UseAuthorization();
        app.UseEndpoints(endpoints => endpoints.MapPluginEndpoints());
    }
}

[ConnectionStringName("Default")]
public class MyDbContext(DbContextOptions<MyDbContext> options) : AbpDbContext<MyDbContext>(options), IPluginsDbContext
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
```

`ConfigurePlugins` maps the tables in the context's default schema (the sample's is `plugins`):

| Table | Holds |
|---|---|
| `plugins` | one row per installed plugin: key, name, version, state, whether it's a system plugin, its folder, its package's hash and name in storage, what its signature showed, the last error |
| `plugin_publishers` | the publishers the site's admins trust: key ID, name, public key, who added it and when |
| `plugin_trust` | the admins' switch for unsigned plugins, one row |
| `plugin_files` | each plugin's files as last saved: name, size, SHA-256 |
| `plugin_manifests` | each plugin's manifest and texts as installed, kept after it's removed while its data stays |
| `plugin_settings` | plugins' settings by key, as `jsonb` |

Your migrations make them, as they make your own tables: `dotnet ef migrations add AbpPlugin010` after adding the
mapping. The sample's `Migrations` folder holds just that. On each new version, add a migration the same way.

The host's tables, the plugins' schemas and the start and migration locks all use the database
`ConnectionStrings:Default` names. Plugins' tables must be on that database, so a plugin's call can commit with the
server's in one transaction.

## Starting

The module doesn't load plugins by itself. Once the application is initialized and its migrations have run, call
`PluginManager.StartAsync`, as the sample's `Program.cs` does:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Host.UseAutofac();
await builder.AddApplicationAsync<MyHostModule>();

WebApplication app = builder.Build();
await app.InitializeApplicationAsync();
await MigrateAsync(app.Services);   // your migrations, the host's tables among them
await app.Services.GetRequiredService<PluginManager>().StartAsync();
await app.RunAsync();
```

`StartAsync` installs or upgrades the system plugins in `PluginHostOptions.SystemPluginFolders`, records the manifest
of any plugin that has none recorded, and loads every enabled plugin (and retries system plugins that failed before).
Servers starting together take turns, under a lock they share. A plugin that can't load is logged and doesn't stop the
start.

The module also runs two hosted services: `PluginReconciler`, which brings this server in line with the plugins table
every `PluginHost:ReconcileSeconds`, and `PluginJobScheduler`, which runs plugins' recurring jobs.
`PluginManager.StopAllAsync` stops and unloads every plugin on this server, each given up to 10 seconds to finish what
it's doing.

## Endpoints

`MapPluginEndpoints(prefix = "/api/plugins")` maps `{prefix}/{key}/{**path}`, any method. Each request is matched
against the enabled plugin's own endpoints, by method and pattern, here rather than in ASP.NET Core's routing tables, so
nothing in MVC's caches holds a plugin's types after it unloads.

| Answer | When |
|---|---|
| 404 | the plugin isn't enabled, or none of its endpoints matches |
| 401 | the path isn't under `public/` and nobody is signed in |
| 413 | a public call's body is over 1 MB |
| 429 | a public call over its limits this minute, with `Retry-After: 60` |
| 503 | the plugin is stopping: being disabled or replaced by an upgrade |
| the endpoint's own | otherwise |

An exception from a plugin is answered as ABP answers one (its status code finder and error info), with no details or
stack trace; a 500 is logged with the plugin's key and the endpoint.

Endpoints under `public/` take anyone's call: a payment provider's webhook, a browser coming back from another site.
They run as no one. The host holds them to a body of 1 MB, to 120 calls a minute from one address and to 3000 a minute
for the plugin, counted through `IPluginCallLimits`; the plugin checks what a call says.

If your sign-in reads a request's form body (an access token posted in a form), call `PublicCalls.Prepare` before
`UseAuthentication`. A public call's body is then capped and kept readable, so the plugin gets the bytes exactly as
they were sent, as a signature over them needs:

```csharp
app.Use((http, next) =>
{
    PublicCalls.Prepare(http);
    return next(http);
});
```

`Prepare` recognizes public calls under `/api/plugins` only.

## Storage

| Where | What |
|---|---|
| Blob container `plugins` | the packages admins install, as `{key}/{version}/{sha256}.zip`; every server fetches a plugin from here |
| Blob container `plugin-files` | plugins' own files, as `{key}/{name}`, each recorded in `plugin_files` |
| `PluginHostOptions.PluginsRoot` | each server's own copy of installed versions, `{PluginsRoot}/{key}/{version}/`: fetched from the store when it's missing or isn't the package the table names, and checked against its hash before it's unpacked |
| The database | the host's tables in your context; each plugin's tables in its own schema |
| Data Protection | the keys plugins' secrets are protected with, under a purpose per plugin |

System plugins come from the server's own folders and never go to storage.

## Options

`PluginHostOptions`, set with `Configure<PluginHostOptions>`:

| Option | Default | What |
|---|---|---|
| `PluginsRoot` | `App_Data/plugins` beside the application | where this server keeps installed versions |
| `SystemPluginFolders` | none | folders, each one plugin's unpacked package, installed or upgraded at every start: the plugins that ship with the application |
| `ModuleVersions` | none | the application's modules and their versions, which plugins' manifests may require (`"requires": { "my-app": ">=2.0.0" }`) |
| `SafeMode` | false | only system plugins load, so a site a plugin breaks still starts and an admin can disable it |

The host reads no configuration key for `SafeMode`. Bind it to one your operators can set without a build, and name that
key in your profile's `SafeModeSetting`, so the host's refusal in safe mode tells admins which setting to turn off:

```csharp
Configure<PluginHostOptions>(options => options.SafeMode = context.Services.GetConfiguration().GetValue("App:SafeMode", false));
// with PluginHostProfile.Current = new() { SafeModeSetting = "App:SafeMode", ... }
```

## Settings

What operators set in configuration (`appsettings.json`, or the environment with `__` for `:`):

| Setting | Default | What |
|---|---|---|
| `ConnectionStrings:Default` | none, required | the database of the host's tables and plugins' schemas, and of the locks taken at start and while a plugin migrates |
| `PluginHost:ReleaseKeys` | none | release keys this server trusts beside the application's built-in ones: an array of public keys, PEM or base64 (`PluginHost__ReleaseKeys__0`). A private key here is ignored, with an error in the log |
| `PluginHost:AllowUnsignedSystem` | false | system plugins load, and packages under the application's own publishers go in, without a release key's signature: for a developer's machine, or an image its operator built |
| `PluginHost:AdminsMayAllowUnsigned` | false | whether the site's admins may allow unsigned plugins, and plugins signed by keys the site doesn't know. While it's off, their switch can't be turned on, and one turned on before counts as off |
| `PluginHost:ReconcileSeconds` | 15 | how often each server brings its plugins in line with the plugins table; at least 5 |
| `Plugins:{publisher}:{app}` | none | a plugin's own configuration, which it reads as `IConfiguration` (`Plugins__acme__ratings__ApiUrl` for `acme.ratings`) |

`AbpClusterOptions.KeyPrefix` (AbpCluster's) goes before the names of the locks the host takes. Keep it the same on
every server of a site.

In the Development environment, an upgrade may bring the version that's installed again: a rebuild of it, so a plugin's
author needn't bump the version for each try. Nowhere else.

## Which packages go in

`PluginTrust` checks every package before it's installed or upgraded, in this order:

1. **Files changed after signing.** A signed package whose files changed, were added or were removed since never goes
   in, whoever signed it and whatever the admins allow.
2. **The application's own publishers** (`PluginHostProfile.IsOwn`). A package under one needs a release key's
   signature, or no signature on a server with `PluginHost:AllowUnsignedSystem`. Anyone can name a plugin so, so a
   publisher's key or the admins' leave doesn't do.
3. **System plugins** (`"system": true` in the manifest, or from `SystemPluginFolders`) need a release key's signature,
   unless the server sets `PluginHost:AllowUnsignedSystem`.
4. **Upgrades keep their signer.** A plugin that went in signed by a release key or a trusted publisher takes only a
   version signed by that same key or by a release key, whether or not unsigned plugins are allowed.
5. **Signed by a release key or a trusted publisher:** in.
6. **Unsigned, or signed by a key the site doesn't know:** in only while `PluginHost:AdminsMayAllowUnsigned` is on and
   the admins allow unsigned plugins, and only when the installed version had no trusted signer. A plugin whose
   publisher's key was removed takes a new version again once the key is trusted again; the admins' leave doesn't do.

Release keys are the application's built-in ones (`PluginHostProfile.ReleaseKeys`) and those in
`PluginHost:ReleaseKeys`. Publishers are added by the site's admins, with the public key they got from the publisher
rather than from a package. Removing a publisher keeps their installed plugins running; no new version of theirs goes
in.

Each installed plugin's row says what its signature showed when it went in: `SignatureKind` (`release`, `publisher`,
`unknown`, `unsigned`), `Signer` and `SignerKeyId`. `PluginTrust.TrustedAsync(row)` says whether that signer is still
trusted.

## Administration

The host has no HTTP API of its own. Map these to your routes, for your admins only:

| Call | What |
|---|---|
| `PluginManager.ListAsync`, `FindAsync(key)` | the installed plugins' rows |
| `PluginManager.InstallAsync(path)` | installs a plugin from a folder or a zipped package |
| `PluginManager.UpgradeAsync(key, path)` | upgrades an installed plugin to a newer version; a disabled one stays disabled, its files and schema upgraded |
| `PluginManager.DisableAsync(key)`, `EnableAsync(key)` | stops and unloads a plugin, or loads it again |
| `PluginManager.UninstallAsync(key)` | removes a plugin: its row and package go; its schema and data, its settings and files, and its recorded manifest (marked removed) stay. System plugins can be disabled, not removed |
| `PluginTrust.PublishersAsync`, `AddPublisherAsync(name, publicKey, by)`, `RemovePublisherAsync(keyId)` | the publishers the site trusts |
| `PluginTrust.AllowUnsignedAsync`, `SetAllowUnsignedAsync(allow)`, `AdminsMayAllowUnsigned` | the admins' switch, and whether the server lets them use it |

A refusal is a `PluginException` whose message says what to change: answer it as a 400 with that message. An install
whose plugin then fails to start isn't refused: its row comes back `Failed`, with the reason in `LastError`. An upgrade
whose new version fails to migrate or seed throws, and the installed version keeps running.

The sample's `SampleAdmin.cs` maps them under `/admin`, with the package as the request's body, each call in a
unit of work of its own that isn't transactional (installing migrates the plugin, which needs connections of its own).
Its `GET /admin/plugins`, `POST /admin/plugins`, `POST /admin/plugins/{key}/upgrade`, `.../disable`, `.../enable`,
`DELETE /admin/plugins/{key}`, `POST /admin/publishers` and `PUT /admin/unsigned` show one way. It lets any signed-in
account call them; an application lets only its admins.

## The seams

The module registers a default for each; register your own in your module's `ConfigureServices`, which runs after
`AbpPluginModule`'s, and yours is the one used:

```csharp
context.Services.Replace(ServiceDescriptor.Singleton<IPluginCallLimits, MySiteLimits>());
context.Services.Replace(ServiceDescriptor.Singleton<IPluginJobSlots, MyJobSlots>());
```

| Seam | Give it when | Default |
|---|---|---|
| `IPluginCaller` | your accounts aren't ABP's current user | `CurrentUserPluginCaller`: ABP's current user, or nobody for the server itself |
| `IPluginCallLimits` | you run several servers, or count limits elsewhere | `InMemoryPluginCallLimits`: counted in this server's memory |
| `IPluginInstallCheck` | plugins declare things that must not clash (pages' keys, settings sections) | none: the host's own checks only |
| `IPluginChanges` | open pages should reload when plugins change | nobody is told |
| `IPluginJobSlots` | you run several servers | `OneServerPluginJobSlots`: claimed in this server's memory |

`IPluginInstallCheck.Check(manifest, enabled)` runs when a plugin is installed, upgraded or enabled, against the
plugins enabled already, and throws a `PluginException` saying what to change. `IPluginChanges.Notify` runs after each
install, enable, disable, upgrade and removal.

## What plugins get from the host

A plugin's container holds its own services, the runtime's (its `PluginContext`, settings, files, secrets,
contributions, units of work, JSON, logging and its configuration section), and only the host services your
application exposes. Expose one in your module:

```csharp
Configure<PluginHostServiceOptions>(options =>
{
    options.Expose<IAccountLookup>();                                     // resolved from a host scope tied to the plugin's
    options.Expose<IReportCatalog>((hostScope, plugin) =>                 // or built per plugin
        new FilteredReports(hostScope.GetRequiredService<IReportCatalog>(), plugin.Key));
});
```

Each plugin scope has a host scope of its own, made with it and disposed with it.

Two services are exposed unless you expose your own of the same type:

| Service | Default |
|---|---|
| `IEventPublisher` | `PluginLocalEvents`: an event goes to every enabled plugin's handlers of its type, local and distributed alike, once the host's unit of work completes (at once when there's none) |
| `IOutboundHttp` | `PluginOutboundHttp`: the HTTP client factory's client named `AbpPlugin.Outbound`, with no rules. It doesn't refuse private or loopback addresses: if your plugins call addresses people type, expose one that does |

## Wiring plugins into the application

The host keeps what plugins register and declare. These are the application's to use:

- **Your events, to plugins.** Hand an event to the enabled plugins' handlers with `PluginEventDelivery.DeliverAsync`:
  the local handlers in the request after your change commits, the distributed ones from your outbox.

  ```csharp
  await delivery.DeliverAsync(typeof(OrderPlaced), order, local: true, alsoUncataloged: false, cancellationToken);
  ```

- **Extension points.** An interface in an assembly the host has (so host and plugin share its type) is an extension
  point. `PluginRuntime.LeaseExtensions<T>` gives your own implementations and every enabled plugin's; dispose the
  lease right after use, since it keeps those plugins in use.

  ```csharp
  await using ExtensionLease<IOrderTotals> lease = runtime.LeaseExtensions<IOrderTotals>(serviceProvider);
  foreach ((string? pluginKey, IOrderTotals totals) in lease.Items)
  {
      // pluginKey is null for your own
  }
  ```

- **Contributions in manifests.** `PluginRuntime.GetContributions("projects.feature@1")` gives every enabled plugin's
  items for that point, from their manifests, as JSON. Validate each against your own schema. `Generation` changes
  whenever a plugin is enabled or disabled: cache what you build from them per generation.
- **Health checks.** A plugin's checks are in its registrations; run them where you show health:

  ```csharp
  foreach (LoadedPlugin plugin in runtime.Enabled)
  {
      foreach (HealthCheckRegistration check in plugin.Registrations.HealthChecks)
      {
          using IDisposable? use = plugin.TryBeginUse();
          if (use is null)
          {
              continue;   // stopping
          }

          await using AsyncServiceScope scope = plugin.CreateScope();
          HealthCheckResult result = await check.Check(scope.ServiceProvider.GetRequiredService(check.CheckType), cancellationToken);
      }
  }
  ```

- **Plugins' settings over HTTP.** `PluginSettingsStore` reads and writes any installed plugin's settings, for a
  plugin's web part or an admin page. Let anyone signed in read keys under `shared.` (`PluginSettingKeys.IsShared`),
  and only your admins the rest. A write records `IPluginCaller.AccountId` as who changed it.
- **Manifests, texts and web bundles.** `plugin_manifests` holds each plugin's manifest and its texts by language, for
  plugins disabled or removed too. `PluginRuntime.EnabledPackages()` gives each enabled plugin's folder, where its
  `web/`, `mobile/`, `messages/` and `docs/` are. The host doesn't serve them.
- **Metrics.** Each `LoadedPlugin.Metrics` counts runs, failures and average time per endpoint, job, handler,
  migration and seeder.

## Several servers

Run as many servers as you like on one database and one blob store:

- Each server brings its plugins in line with the plugins table: what another server installed, upgraded, disabled or
  removed happens here too, and what couldn't load (storage out of reach) is tried again.
- One server at a time installs system plugins at start, and migrates and seeds a given plugin; the others wait, then
  find it done.
- A recurring job never overlaps itself, on any server, and each occurrence is claimed through `IPluginJobSlots`.
- Give the servers a shared `IPluginCallLimits` and `IPluginJobSlots`. With the defaults, each server counts limits
  and claims slots alone: the site allows more public calls than its limits say, and a job's occurrence may run on
  more than one server.
- Keep blob storage where every server reaches it, not on one server's disk.
- Keep Data Protection's keys where every server reads them, or a server can't read a secret another protected.

A package that's gone from storage (a database moved without the site's storage) leaves its plugin enabled, with the
reason in `LastError`: it loads by itself once the storage is back, or an upgrade with that same version's package
puts it back. A package in storage whose hash isn't the one recorded never runs: the plugin is `Failed` everywhere until
a version is installed again.

## Checking plugins against the host's assemblies

A plugin built against a newer SDK or contracts than the server has would fail when its code first reaches what's
missing. The host can refuse it at install instead, saying what it lacks: list the shared assemblies' name prefixes in
the profile. With the defaults, nothing is checked.

```csharp
PluginHostProfile.Current = new PluginHostProfile
{
    SharedAssemblyPrefixes = ["AbpPlugin.", "MyApp.Contracts"],
    FallsBackAttribute = ("AbpPlugin", "ToleratesOlderHostsAttribute"),
};
```

With `FallsBackAttribute` set, a plugin whose assembly has that attribute is installed anyway, with what it lacks
logged: it says it checks for what it uses and falls back. Set the profile once, before any plugin is read: a module
initializer, or the first lines of `Program.cs`.

## An application's own names

An application whose packages are already out keeps their names: it sets `PluginPackageFormat.Current` (its
manifest's, signature's and mark's names, its signed prefix, its package extensions) and its `PluginHostProfile`. See
[the package format](package-format.md) and [moving onto AbpPlugin](moving-onto-abp-plugin.md).
