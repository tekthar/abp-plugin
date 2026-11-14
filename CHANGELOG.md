# Changelog

All notable changes to AbpPlugin are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the packages follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

## [0.2.0] - 2026-11-14

Plugins' screens of their own: a server package that serves their browser bundles and lists them, and an npm package
for the page. `AbpPlugin`, `AbpPlugin.Sdk`, `AbpPlugin.Packaging`, `AbpPlugin.Build` and `AbpPlugin.Sign` don't change.

### Added

- `AbpPlugin.Web`, plugins' web parts ([docs/web.md](docs/web.md)): `AbpPluginWebModule`, and `MapPluginWeb`, which
  serves each enabled plugin's browser bundle from the folder its version is installed in
  (`/plugins-web/{key}/{folder}/{**file}`, every installed version's, nothing outside `web/`, `Cache-Control:
  no-cache`) and lists the enabled plugins' Module Federation remotes (`/api/plugins-web/remotes`). An application adds
  its own sections to each plugin in the list, or leaves one out, with `IPluginWebSections`; `PluginWebRemotes` gives the
  remotes to an application that builds its own list; `MapPluginPartFiles` serves another folder of the plugins the
  same way; `PluginWebOptions` sets the prefix, the folder and the cache header.
- `@abpjs/plugin-web`, the browser package beside it (npm): `createPluginLoader` registers the listed remotes
  with Module Federation's runtime and loads what they expose, sharing the application's own modules;
  `fetchPluginRemotes` reads the list; `/react` gives each exposed component as a React lazy component, dropped when
  its remote changes.
- The sample plugin has a web part (`samples/AbpPlugin.Sample/web`, its page as `./Page`), and the sample host a page
  that shows the enabled plugins' pages; `/sample/sign-in` signs the page in. The sample's `ping` gives its version.

## [0.1.1] - 2026-11-14

### Added

- `PluginPackageFormat.Unsigned`: files at a package's root that the digest leaves out besides the signature and the
  marks, for ones added after the publisher signed, such as a marketplace's countersignature. Without it, a
  countersigned package never matched its signature once unpacked.

### Fixed

- The digest leaves out a format's former marks (`FormerMarkers`) as it leaves out the mark, so a folder a host
  unpacked under a former mark's name still matches its signature.

## [0.1.0] - 2026-11-14

The first release: the plugin host Toda had built in, as five packages with neutral names and AbpPlugin's own package
format by default. Toda moves onto them keeping its own names ([moving onto AbpPlugin](docs/moving-onto-abp-plugin.md)).

### Added

- `AbpPlugin`, the host:
  - `AbpPluginModule`, on ABP's ASP.NET Core, EF Core and blob storing modules and `AbpClusterModule`. It registers the
    seams' defaults, the plugins' reconciler and the recurring jobs' scheduler.
  - `PluginManager`: `StartAsync` (system plugins from `PluginHostOptions.SystemPluginFolders` installed or upgraded,
    then every enabled plugin loaded, under a lock servers share), `InstallAsync` (a folder or a zip), `UpgradeAsync`
    (the new version beside the old, migrated, then swapped in one step), `EnableAsync`, `DisableAsync` (the code
    unloaded), `UninstallAsync` (schema and data kept), `ReconcileAsync`, `ListAsync`, `FindAsync`, `StopAllAsync`.
  - `PluginTrust`: release keys (`PluginHost:ReleaseKeys` and the application's own), trusted publishers
    (`AddPublisherAsync`, `RemovePublisherAsync`, `PublishersAsync`), the admins' switch for unsigned plugins
    (`SetAllowUnsignedAsync`, only where `PluginHost:AdminsMayAllowUnsigned` lets them), and the rules: a package whose
    files changed after signing never goes in, system plugins and the application's own publishers need a release key,
    an upgrade keeps the plugin's signer.
  - `PluginRuntime`: the enabled plugins, `LeaseExtensions<T>` for extension points, `GetContributions` from
    manifests, `Generation`, and `GetUnloadReport`, which shows whether released versions were collected.
  - `MapPluginEndpoints`: `/api/plugins/{key}/{**path}`, matched against each plugin's own endpoints. Signed-in callers
    only, except under `public/`, where `PublicCalls` caps a body at 1 MB and calls at 120 a minute from an address and
    3000 a minute for the plugin.
  - Each plugin version in a collectible `PluginLoadContext` with its own DI container, sharing the host's assemblies.
    Plugin calls (endpoints, jobs, event handlers) in one unit of work with the server's (`PluginCalls`).
  - Plugins' settings (`PluginSettingsStore`), files (blob container `plugin-files`, recorded in `plugin_files`),
    secrets (Data Protection, a purpose per plugin), packages (blob container `plugins`), manifests and texts as
    installed (`plugin_manifests`), events (`PluginEventDelivery`) and recurring jobs (`PluginJobScheduler`, on Cronos).
  - The seams `IPluginCaller`, `IPluginCallLimits`, `IPluginInstallCheck`, `IPluginChanges` and `IPluginJobSlots`,
    with defaults for one server (`PluginHostDefaults`), and the services plugins get (`PluginHostServiceOptions`,
    with `IEventPublisher` and `IOutboundHttp` by default).
  - `IPluginsDbContext` and `PluginsModel.ConfigurePlugins`: the tables `plugins`, `plugin_publishers`,
    `plugin_trust`, `plugin_files`, `plugin_manifests` and `plugin_settings`, in the host application's context.
  - `PluginHostOptions` (`PluginsRoot`, `SystemPluginFolders`, `ModuleVersions`, `SafeMode`) and `PluginHostProfile`
    (the application's name in messages, secrets' purpose, manifest sections, entry runners, shared assemblies and
    their former names, former keys and modules, pinned schemas, built-in release keys, its own publishers).
- `AbpPlugin.Sdk`: `PluginEntry`, `PluginContext`, `IPluginServices` (`AddDbContext`, `AddSeeder`,
  `AddEventHandler`, `AddLocalEventHandler`, `AddRecurringJob`, `AddExtension`, `AddEndpoint`, `AddPublicEndpoint`,
  `AddHealthCheck`), `PluginDbContext` with `PluginSchemaNames` and `UsePluginSchema`, `IPluginEndpoint`,
  `PluginRequest`, `PluginResults`, `IPluginJson`, `IPluginSettings` with typed extensions, `IPluginFiles`,
  `ISecretProtector`, `IEventPublisher`, `IEventHandler<T>`, `ILocalEventHandler<T>`, `IRecurringJob`,
  `IPluginSeeder`, `IPluginHealthCheck`, `IPluginContributions`, `IPluginUnitsOfWork`, `PluginUnitOfWorkAttribute`,
  `IOutboundHttp`, `AppIds` and `ToleratesOlderHostsAttribute`.
- `AbpPlugin.Packaging`, on .NET's own assemblies only: `PluginPackageFormat` (AbpPlugin's names by default:
  `plugin.json`, `plugin-signature.json`, `.plugin-package`, `abp-plugin-signature/1`, `.zip`; an application sets its
  own), `PluginSignature` (keys, key IDs, the digest, signing and verifying), `PluginSignatureFile`,
  `PluginPackageManifest`, `SemanticVersion` with ranges, `PluginException`, and `PluginSigningTool`, the signing
  tool's commands for `abp-plugin-sign` and for an application's own tool in its own format.
- `AbpPlugin.Build`: targets that pack a plugin after its build into `artifacts/plugins/{key}-{version}/` and
  `artifacts/{key}-{version}.zip` (its assembly and symbols, its manifest with the version, its web and phone bundles,
  its texts and its docs, the host's assemblies left out), sign it when given a key, and build its web part when asked.
- `AbpPlugin.Sign`, the .NET tool `abp-plugin-sign`: `keygen`, `sign`, `verify` and `keyid`.
- A sample host (`samples/AbpPlugin.SampleHost`) with its migration `AbpPlugin010`, a sample plugin on the SDK alone
  (`samples/AbpPlugin.Sample`), unit tests, and host tests on PostgreSQL that install, upgrade, unload, enable and
  remove the sample plugin, and refuse unsigned, unknown and changed packages.

### Known gaps

What the host keeps but leaves to the application, for a later version or for each application:

- No HTTP API or pages for administration: an application maps `PluginManager` and `PluginTrust` to its own routes,
  as the sample's `SampleAdmin` does.
- Plugins' health checks, web bundles, manifest sections and texts are kept, not shown or served: the application reads
  them from `PluginRuntime` and `plugin_manifests`.
- No HTTP API for plugins' settings: the application maps `PluginSettingsStore`, letting anyone signed in read keys
  under `shared.` and only its admins the rest.
- No purge: a removed plugin's schema and data stay until someone drops them.
- The default `IOutboundHttp` is a plain client: it doesn't refuse private or loopback addresses. An application whose
  plugins call addresses people type exposes one of its own.
