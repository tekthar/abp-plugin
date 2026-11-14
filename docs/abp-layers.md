# For ABP developers: where ABP's layers are

AbpPlugin is a plugin host for an ABP application, closer to infrastructure than to an application module: it has a
domain (plugins, their trust, their runtime) and tables, but no application services or admin API of its own, because
the host decides who manages plugins and from which page. This page maps ABP's module layers onto its packages, so
you can find things where you'd look for them.

| ABP's layer (`abp new -t module`) | In AbpPlugin |
|---|---|
| `*.Domain.Shared` (constants, enums, error codes) | `AbpPlugin.Packaging`: the package format (`PluginPackageFormat`, `PluginPackageManifest`), `PluginSignature`, `SemanticVersion`, `PluginException`; `PluginSignatureKinds` in `AbpPlugin` |
| `*.Domain` (entities, domain services) | `AbpPlugin`: the entities in `PluginsModel.cs` (plugins, publishers, trust, files, manifests, settings), `PluginManager` (install, upgrade, enable, disable, remove), `PluginTrust`, `PluginRuntime` |
| `*.Application.Contracts` | None: a host's admin page calls `PluginManager` and `PluginTrust` itself. What they give back (`PluginInstallation`…) is in `AbpPlugin` |
| `*.Application` | None, for the same reason; `samples/AbpPlugin.SampleHost/SampleAdmin.cs` shows the dozen endpoints a host writes |
| `*.EntityFrameworkCore` | `AbpPlugin`: `IPluginsDbContext` and `PluginsModel.ConfigurePlugins(modelBuilder)`, in schema `plugins`; the host's own context and migrations hold the tables. Each plugin's own tables are in a schema of its own, migrated by the host |
| `*.HttpApi` | `MapPluginEndpoints` (each plugin's own endpoints under `/api/plugins/{key}`), and `AbpPlugin.Web`'s `MapPluginWeb` (plugins' browser bundles and their remotes) |
| `*.HttpApi.Client` | None |
| `*.Web` / `*.Blazor` | No pages of its own. `@abpjs/plugin-web` (npm) is the page's side: it loads the plugins' screens into the application's own pages |
| What a plugin references | `AbpPlugin.Sdk` (`PluginEntry`, `IPluginServices`, `PluginDbContext`, endpoints, events, jobs, settings, files, secrets) |
| Tooling | `AbpPlugin.Build` (MSBuild targets that pack a plugin) and `AbpPlugin.Sign` (the `abp-plugin-sign` tool) |

## What works the ABP way

- **Modules:** `AbpPluginModule` and `AbpPluginWebModule`, each with `[DependsOn]`; the host's services are registered
  by ABP's conventions.
- **The database:** replace `IPluginsDbContext` with your own context (`ReplaceDbContext<IPluginsDbContext>()`), call
  `PluginsModel.ConfigurePlugins(builder)` in its `OnModelCreating`, and add a migration as for your own tables.
- **Files go through ABP's BLOB storing:** packages in the `plugins` container, plugins' files in `plugin-files`; any
  provider you configure for them works.
- **Units of work:** a plugin's endpoint, job or event handler runs in an ABP unit of work with the server's, so what
  the plugin and the server's services change commits together.
- **Several servers:** ABP's distributed lock (`IAbpDistributedLock`) keeps one server at a time migrating a plugin or
  running a recurring job's occurrence.
- **Options and settings:** `PluginHostOptions` and `PluginWebOptions` in code, and the `PluginHost:` section of
  configuration (`ReleaseKeys`, `AdminsMayAllowUnsigned`, `ReconcileSeconds`).
- **Replacing a part:** each seam (`IPluginCaller`, `IPluginCallLimits`, `IPluginInstallCheck`, `IPluginChanges`,
  `IPluginJobSlots`, `IPluginWebSections`) is a service you register in `ConfigureServices`; the defaults are for one
  server (`PluginHostDefaults`).

## Where it differs, and why

- **A plugin isn't an ABP module.** ABP's plug-in modules load at start and stay loaded; a plugin here is installed
  while the application runs, in a collectible load context with a DI container of its own, so it can be upgraded and
  unloaded without a restart. It says what it adds through `IPluginServices`, and gets only the services the host
  exposes (`PluginHostServiceOptions`).
- **No application services or admin API:** who may install a plugin, and on which page, is the host's; the host calls
  `PluginManager` with its own checks, as the sample's `SampleAdmin` does.
- **Who calls is `IPluginCaller`, not ABP permissions:** a plugin's endpoints need a signed-in caller by default
  (`ICurrentUser`), and the host's implementation can apply its permissions or anything else.
- **Signed packages:** trust is ECDSA signatures checked by `PluginTrust` against release keys and publishers' keys, a
  concept ABP doesn't have.
- **Refusals are `PluginException`,** with a message for the person; the host maps it to its own error answers.
- **Texts:** messages are in English in code; there are no localization resources.
- **PostgreSQL only:** each plugin's tables live in a schema of its own, which the host creates and migrates.
