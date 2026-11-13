# Building plugins

A plugin is a class library on `AbpPlugin.Sdk`, built into a package by `AbpPlugin.Build` and signed with
`abp-plugin-sign`. `samples/AbpPlugin.Sample` uses every part of the SDK once; this page follows it.

## The project

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PluginKey>acme.ratings</PluginKey>
    <PluginVersion>1.0.0</PluginVersion>
    <Version>$(PluginVersion)</Version>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="AbpPlugin.Sdk" Version="0.1.0" />
    <PackageReference Include="AbpPlugin.Build" Version="0.1.0" />
    <!-- Shared with the host at run time; referenced for compiling and for dotnet ef. -->
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>

</Project>
```

`PluginKey` is the plugin's app ID: the publisher's slug, a dot and the app's slug, as `acme.ratings`. A slug is 1 to
64 lowercase letters, digits and single hyphens, starting and ending with a letter or a digit. The key is the plugin's
for good: a version under another key is another plugin. Use your own publisher's name; a host may keep some
publishers' names for its application's own plugins.

`AbpPlugin.Build` sets `EnableDynamicLoading`, so the build's output holds what the plugin needs to load on its own.

Beside the project, `plugin.template.json` is the manifest, with `__VERSION__` where the version goes:

```json
{
  "key": "acme.ratings",
  "name": "Ratings",
  "description": "Ratings for orders.",
  "version": "__VERSION__",
  "vendor": { "name": "Acme", "url": "https://acme.example" },
  "requires": { "my-app": ">=2.0.0" },
  "server": {
    "assembly": "Acme.Ratings.dll",
    "entry": "Acme.Ratings.RatingsPlugin"
  }
}
```

[The package format](package-format.md) lists every field.

## The entry

`server.entry` names a class derived from `PluginEntry`. Its `ConfigureServices` says what the plugin adds; the host
keeps track of each registration, so disabling the plugin removes all of it. From the sample:

```csharp
public sealed class SamplePlugin : PluginEntry
{
    public override void ConfigureServices(IPluginServices services) => services
        .AddDbContext<SampleDb>()
        .AddSeeder<SampleSeeder>()
        .AddLocalEventHandler<VisitRecorded, VisitHeard>()
        .AddEndpoint<Ping>("GET", "ping")
        .AddEndpoint<RecordVisit>("POST", "visit")
        .AddHealthCheck<SampleCheck>("sample");
}
```

| Registration | What it adds |
|---|---|
| `Services` | the plugin's own container, for its internal services |
| `AddDbContext<T>()` | its EF Core context, migrated in its schema each time the plugin loads |
| `AddSeeder<T>()` | an `IPluginSeeder`, run after the migrations each time the plugin loads: install, upgrade, enable, a server's start. It must be idempotent |
| `AddEndpoint<T>(method, pattern)` | an `IPluginEndpoint` at `/api/plugins/{key}/{pattern}`, for signed-in callers. Patterns under `public/` are refused here |
| `AddPublicEndpoint<T>(method, pattern)` | an `IPluginEndpoint` anyone may call, at `/api/plugins/{key}/public/{pattern}` |
| `AddLocalEventHandler<TEvent, T>()` | an `ILocalEventHandler<TEvent>`: in the same process, right after the change commits; not retried |
| `AddEventHandler<TEvent, T>()` | an `IEventHandler<TEvent>`: from the publishing module's outbox, at least once; drop what you've seen |
| `AddRecurringJob<T>(cron)` | an `IRecurringJob` on a cron schedule, in UTC: five fields, or six with seconds first |
| `AddExtension<TPoint, T>()` | an implementation of an extension point the host or another plugin owns |
| `AddHealthCheck<T>(name)` | an `IPluginHealthCheck`, for the host application's health page |

Endpoints, seeders, handlers, jobs, extensions and checks are made by the plugin's container, so they take what they
need in their constructors.

Which events reach which handlers is the host application's to say. In a host with no outbox of its own (the
default), an event a plugin publishes reaches every enabled plugin's handlers of its type, local and distributed
alike, once the call that published it commits.

## Its tables

A context derives from `PluginDbContext` and maps its model in `ConfigureModel`. The host gives it the plugin's schema
and the server's connection; mapping an entity to any other schema throws.

```csharp
public sealed class SampleDb(DbContextOptions<SampleDb> options) : PluginDbContext(options)
{
    public DbSet<Visit> Visits => Set<Visit>();

    protected override void ConfigureModel(ModelBuilder modelBuilder) => modelBuilder.Entity<Visit>(b =>
    {
        b.ToTable("visits");
        b.Property(v => v.Id).ValueGeneratedNever();
        b.Property(v => v.Note).HasMaxLength(200).IsRequired();
    });
}
```

The schema is `PluginSchemaNames.For(key)`: `plg_` and the key with the dot written `__` and each hyphen `_`
(`plg_acme__ratings`; `acme.jira-sync` is `plg_acme__jira_sync`). A name over PostgreSQL's 63 bytes is shortened with
a hash. Its migrations history is in the same schema.

Migrations are ordinary EF Core migrations. For `dotnet ef`, give a design-time factory with the schema the host will
give:

```csharp
public sealed class SampleDbFactory : IDesignTimeDbContextFactory<SampleDb>
{
    public SampleDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SampleDb>()
            .UseNpgsql("Host=localhost;Database=design", npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", PluginSchemaNames.For("acme.abp-sample")))
            .UsePluginSchema(PluginSchemaNames.For("acme.abp-sample"))
            .Options);
}
```

```bash
dotnet ef migrations add Visits
```

One server at a time migrates and seeds a plugin; the others wait, then find it done. Removing a plugin keeps its
schema and data, so installing it again finds them.

## Endpoints

An endpoint reads the call and returns its answer. The server writes the answer once the call has committed, so a
commit that fails is the caller's error, never a success. Don't write the response yourself.

```csharp
public sealed class RecordVisit(SampleDb db, IPluginSettings settings, IPluginFiles files, ISecretProtector secrets, IEventPublisher events, IPluginJson json)
    : IPluginEndpoint
{
    public async Task<IResult> HandleAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        string note = request.Query("note") is { Length: > 0 } given ? given : "visit";
        Visit visit = new() { Id = Guid.NewGuid(), Note = note };
        db.Visits.Add(visit);
        await db.SaveChangesAsync(cancellationToken);
        await settings.SetAsync("last", note, cancellationToken: cancellationToken);
        await settings.SetAsync("secret", secrets.Protect(note), cancellationToken: cancellationToken);
        await files.SaveAsync("last.txt", new MemoryStream(Encoding.UTF8.GetBytes(note)), cancellationToken);
        await events.PublishAsync(new VisitRecorded(visit.Id), cancellationToken);
        return json.Result(new { visit.Id });
    }
}
```

- `PluginRequest` has the request (`Http`: query, headers, body), `RouteValues` with `Route(name)`, `Query(name)`, and
  the caller (`User`; nobody under `public/`).
- A pattern is a route template, as `orders/{id}`. The host matches its segments and parameters; it doesn't apply
  route constraints, so check a value in the endpoint.
- Answer JSON with `IPluginJson.Result(value)`, not `Results.Json` or `Results.Ok(value)`: the host's shared serializer
  options would keep your types after the plugin unloads. Read JSON with `IPluginJson.ReadAsync<T>`. `Results` with no
  value (`Results.NoContent()`, `Results.NotFound()`) are fine.
- `PluginResults.ServerSentEventsAsync(events, cancellationToken)` streams server-sent events: the first is read in
  the call, so a refusal before anything is sent is the endpoint's to answer; the rest stream once the call has
  committed.
- A public endpoint checks every call itself: a signature over the body, or asking the caller's system about what it
  says. Its body is at most 1 MB, readable from its start, and the host limits how often an address and the plugin are
  called.

An exception from an endpoint is answered as ABP answers one: its status from ABP's status code finder, and no
details or stack trace. For a refusal the caller should read, return a result (`Results.BadRequest()`,
`json.Result(error, 400)`), or throw an exception whose message ABP shows, such as `UserFriendlyException`. Anything
ABP doesn't map is a 500 that says nothing of the inside, and the host logs it.

## Units of work

A call that may change something is one unit of work with the server: an endpoint other than GET and HEAD, a recurring
job's run, an event handler. What the plugin saves and what the server's services it calls change commit together when
the call ends, or not at all when it throws. A GET or HEAD endpoint is never transactional.

Work that keeps its progress as it goes (an import) is marked so, and each save then commits by itself:

```csharp
[PluginUnitOfWork(IsTransactional = false)]
public sealed class NightlyImport(IPluginUnitsOfWork units) : IRecurringJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken) =>
        units.RunAsync(async (services, ct) =>
        {
            ImportDb db = services.GetRequiredService<ImportDb>();
            // one batch: committed when it returns, undone alone when it throws
            await db.SaveChangesAsync(ct);
        }, cancellationToken);
}
```

Contexts that join a batch come from the batch's services, its first argument; those the class was given commit by
themselves.

## What a plugin gets

| Service | What |
|---|---|
| `PluginContext` | its key, version, schema, connection string and folder |
| `IPluginSettings` | its own settings by key, as JSON (typed with `GetAsync<T>` and `SetAsync<T>`). A key is up to 200 letters, digits, `.`, `-` and `_`; a value up to 64 KiB; up to 1000 keys. Keys under `shared.` may be read by anyone signed in through the host application's API, the rest only by its admins. Settings are configuration: data goes in tables |
| `IPluginFiles` | its own files in the host's blob storage, so they outlive the server that saved them. A name is up to 200 letters, digits, `.`, `-` and `_`, not starting with a dot |
| `ISecretProtector` | protects what it stores (tokens, passwords) with the host's Data Protection keys, under a purpose of its own; `Unprotect` gives null when the keys changed |
| `IEventPublisher` | publishes events to their handlers, as the host application delivers them |
| `IOutboundHttp` | an `HttpClient` for outgoing calls, with the host application's rules |
| `IPluginContributions` | other plugins' manifest items for the extension points this plugin owns |
| `IPluginUnitsOfWork` | units of work of its own, one per batch |
| `IPluginJson` | JSON answers and reads that unload with the plugin |
| `IConfiguration` | its own configuration section, `Plugins:{publisher}:{app}` (`Plugins__acme__ratings__ApiUrl` from the environment) |
| `ILogger<T>` | the host's logging |
| what the host exposes | the host application's own services, only those it chose |

A plugin owns extension points named by its key, a dot, a name and a version: `acme.ratings.badge@1`. Other plugins
contribute to one in their manifests (`"acme.ratings.badge@1": [ { ... } ]`), and the owner reads the items with
`IPluginContributions.Get("acme.ratings.badge@1")`. Asking for any other point throws. The items are other plugins'
JSON: validate each, and skip the bad ones. `Generation` changes whenever a plugin is enabled or disabled.

## Unloading

Disabling or upgrading a plugin stops new calls, waits up to 10 seconds for the ones running, and unloads its code. It
can only be collected if nothing outside it still holds its types:

- Answer and read JSON with `IPluginJson`, and read typed settings through the SDK's extensions, which use options of
  their own.
- Don't hand your types or delegates to a host service that lives longer than the call (a static cache, an event of a
  singleton).
- Don't start threads or timers of your own: use a recurring job, and stop when its token is cancelled (on disable, or
  when the server stops).

The host loads its own copy of any assembly it has (ABP, ASP.NET Core, EF Core, Npgsql, the SDK), whatever the plugin
carries: a plugin can't bring another version of one. Everything else loads from the plugin's folder.

## Building and packing

`dotnet build` packs the plugin after the build, by default into the project's `artifacts/`:

```
artifacts/plugins/acme.ratings-1.0.0/   the package's folder
artifacts/acme.ratings-1.0.0.zip        the same, zipped, to upload
```

The package holds the build's output (the plugin's own symbols beside its assembly; other symbols, design-time tools,
satellite resource assemblies and the host's own assemblies left out), the manifest with the version in place, the web
bundle (`web/dist` as `web/`), the phone's (`mobile/dist` as `mobile/`), the texts (`web/messages/*.json` and
`messages/*.json` as `messages/`) and the docs (`docs/`), each when there is one.

| Property | Default | What |
|---|---|---|
| `PluginKey` | required | the plugin's app ID |
| `PluginVersion` | required | its version, written into the manifest |
| `AbpPluginRoot` | the project's folder | the folder that holds `web/`, `mobile/`, `messages/` and `docs/` |
| `AbpPluginDocsDir` | `$(AbpPluginRoot)docs\` | the plugin's docs: Markdown pages and the images they show |
| `AbpPluginArtifacts` | `$(AbpPluginRoot)artifacts\` | where the package goes, as `plugins/{key}-{version}/` and `{key}-{version}.zip` |
| `AbpPluginPackageDir` | `$(AbpPluginArtifacts)plugins\{key}-{version}\` | the package's folder itself |
| `AbpPluginZip` | `true` | `false` packs the folder only |
| `AbpPluginBuildWeb` | `false` | `true` builds `web/` first. Without `node_modules` it installs: `npm ci` when it has only npm's lock file, else `pnpm install` (frozen to `pnpm-lock.yaml` when there is one). Then `run build`, with the same tool |
| `AbpPluginExcludeHostAssemblies` | `true` | `false` keeps the host's own assemblies in the package (the host loads its own copies anyway) |
| `AbpPluginHostAssemblies` | `AbpPlugin.Sdk.dll;Abp*.Contracts.dll` and EF Core's three | the host's own assemblies, by file name; wildcards allowed |
| `AbpPluginMoreHostAssemblies` | none | more of them: a host application's own SDK and contracts |
| `AbpPluginSigningKey` | none | a private key (`private.pem`) to sign the package with, before it's zipped |
| `AbpPluginSigner` | none | the name the signature carries, which sites show; needed with a key |
| `AbpPluginSignCommand` | `abp-plugin-sign` | the command that signs, as `dotnet tool run abp-plugin-sign` for a tool manifest's |
| `AbpPluginManifestFile` | `plugin.json` | the manifest's name in the package |
| `AbpPluginManifestTemplate` | `plugin.template.json` beside the project | the manifest's template |
| `AbpPluginDisplayName` | `Plugin` | the word the build's messages start with |
| `AbpPluginZipHint` | none | what the message after the zip's path says, as where to upload it |
| `AbpPluginPropertyPrefix` | `AbpPlugin` | the prefix of the properties the build's errors and warnings name |

The last five are for a host application's own package of these targets, which keeps its names: see
[moving onto AbpPlugin](moving-onto-abp-plugin.md). A zip without `web/dist/remoteEntry.js`, for a plugin that has a
`web/package.json`, warns that its web part wasn't built.

## Signing

Install the tool and make a key pair once:

```bash
dotnet tool install -g AbpPlugin.Sign
abp-plugin-sign keygen keys
```

`keys/private.pem` signs: keep it secret, out of the repository. `keys/public.pem` is what sites trust: give it to
their admins yourself, apart from any package. `keygen` doesn't replace a key that's there.

Sign as the build packs:

```bash
dotnet build -c Release -p:AbpPluginSigningKey=keys/private.pem -p:AbpPluginSigner="Acme"
```

or sign a packed folder, then zip it:

```bash
abp-plugin-sign sign --key keys/private.pem --signer "Acme" artifacts/plugins/acme.ratings-1.0.0
abp-plugin-sign verify --key keys/public.pem artifacts/plugins/acme.ratings-1.0.0
abp-plugin-sign keyid keys/public.pem
```

`sign` writes `plugin-signature.json` into each folder named. `verify` says `good` or `BAD` for each folder, and exits
1 when any is bad. `keyid` prints the key's ID, as a site shows it. Signing comes last: any change to the folder after
it breaks the signature, and a site refuses the package.

## Versions

A version is one to three numbers, `major.minor.patch`. An upgrade must be newer than what's installed; only a
Development server takes the same version again, as a rebuild. A prerelease part is ignored when versions are
compared: `1.0.0-beta.1` counts as `1.0.0`.

`requires` names what the plugin runs on: the host application's modules (as it lists them) or other plugins by app
ID, each with a range of comparators that must all hold, as `>=1.2.0 <2.0.0`. A plugin whose requirement isn't
installed, enabled or in range is refused, saying which.

A plugin that uses something newer servers have, and checks for it first, may say so with
`[assembly: ToleratesOlderHosts]`. It counts only on a host that checks plugins against its assemblies and names the
attribute (see [hosting](hosting.md#checking-plugins-against-the-hosts-assemblies)).

## Trying it on the sample host

The sample host stands its own sign-in in with a header, `X-Sample-Account` (any Guid). Run it against a local
PostgreSQL, trust your key, and install:

```bash
dotnet run --project samples/AbpPlugin.SampleHost
curl -H "X-Sample-Account: 00000000-0000-0000-0000-0000000000a1" -H "Content-Type: application/json" \
  -d "{\"name\": \"Acme\", \"publicKey\": $(jq -Rs . < keys/public.pem)}" http://localhost:5000/admin/publishers
curl -H "X-Sample-Account: 00000000-0000-0000-0000-0000000000a1" \
  --data-binary @artifacts/acme.ratings-1.0.0.zip http://localhost:5000/admin/plugins
curl -H "X-Sample-Account: 00000000-0000-0000-0000-0000000000a1" http://localhost:5000/api/plugins/acme.ratings/ping
```

The last line calls one of the plugin's endpoints (`ping` here). Use the address the host prints if it isn't
`http://localhost:5000`. `POST /admin/plugins/{key}/upgrade` takes the next
version's zip; `.../disable`, `.../enable` and `DELETE /admin/plugins/{key}` do the rest.
