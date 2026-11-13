# 0001. AbpPlugin as a package

- **Status:** Accepted
- **Date:** 2026-11-14
- **Revisit at:** AbpPlugin 1.0, or when a second application runs plugins on it in production

## Context

A plugin host was built into one ABP application. It installs plugin packages while the application runs, loads each
version into an unloadable load context with a DI container and a database schema of its own, checks packages'
signatures, runs plugins' endpoints, jobs and event handlers in one transaction with the server's, and keeps several
servers in step. Other ABP applications want the same. They can't take it from that application as it was: its
product's name was in the package format and the messages, its seams were its own services, and its tables were in
its own context.

ABP's plug-in modules don't cover it. They load module assemblies from a folder (`PlugInSources`) when the application
starts; they can't be unloaded, have no schema of their own, and change only with a restart.

Packages are already out in that application's format, signed by its publishers. A host that reads only another format
would refuse them, and a signature made over that format's names doesn't verify under another's. Its plugins' secrets
are protected under a Data Protection purpose of its own, and its plugins' schemas are named by their keys.

## Decision

**AbpPlugin is that host as packages with neutral names: five packages; a package format whose names the application
gives, with AbpPlugin's own by default; seams with defaults for one server; PostgreSQL and EF Core; and the tables in
the host application's own context.** Moving the application it came from onto the packages is a step of its own
([moving onto AbpPlugin](../moving-onto-abp-plugin.md)).

### The packages

| Package | Holds | Why apart |
|---|---|---|
| `AbpPlugin` | The host: the module, the lifecycle, trust, the runtime, the dispatcher, the seams, the table mapping | What an application references |
| `AbpPlugin.Sdk` | What a plugin references: its entry, registrations, context, endpoints, events, jobs, settings, files, secrets, health checks, seeders, contributions | A plugin needs nothing of ABP's, so it builds against a small surface the host shares with it |
| `AbpPlugin.Packaging` | The format: names, keys, digest, signatures, manifests' key and version, versions and ranges, and the signing tool's commands | Only .NET's own types, so a marketplace, a build or a signing tool reads a package as the host does |
| `AbpPlugin.Build` | MSBuild targets that pack, sign and zip a plugin after its build | A plugin's build makes the package a host installs, with no script of its own |
| `AbpPlugin.Sign` | The tool `abp-plugin-sign`: `keygen`, `sign`, `verify`, `keyid` | What a publisher and a build run, outside any host |

No product's name is in any of them; a check in CI holds it, in the repository and in the packed packages.

### The format's names

The format's file names and the bytes a signature is made over are the application's to give, once, through
`PluginPackageFormat.Current`, before any package is read or signed. A build, a signing tool and a marketplace working
for that application say the same. An application with no packages yet keeps AbpPlugin's own:

| What | AbpPlugin's default |
|---|---|
| The manifest, at the package's root | `plugin.json` |
| The publisher's signature, beside it | `plugin-signature.json` |
| The mark a server writes on a package it unpacked | `.plugin-package` |
| What a signature is made over, before the digest | `abp-plugin-signature/1\n` |
| The extensions a zipped package comes in | `.zip` |

Former names of the manifest and the mark are read too, so packages made before a rename still install. The signed
prefix ties a signature to its format: a signature made for one format can't be passed off as one of another's.

What the application says of itself beyond the format is its `PluginHostProfile`, set the same way: its name in
messages, the Data Protection purpose of plugins' secrets, the manifest sections it reads, the entries of its own SDK
and how to run them, the assemblies it shares with plugins and their former names, plugins' former keys and pinned
schemas, its built-in release keys, and which publishers are its own. Both are static: manifests and packages are read
where no services are at hand (a build, a tool, a manifest's own checks).

### The seams

| Seam | What it answers | Default |
|---|---|---|
| `IPluginCaller` | who the host acts for, recorded with a setting's change | ABP's current user |
| `IPluginCallLimits` | whether a public endpoint's call fits its limits | counted in this server's memory |
| `IPluginInstallCheck` | the application's own checks on a plugin coming in | none |
| `IPluginChanges` | telling the application that plugins changed | nobody |
| `IPluginJobSlots` | one run of a recurring job's occurrence for the whole site | claimed in this server's memory |
| `IEventPublisher` (given to plugins) | where a plugin's events go | the enabled plugins' handlers, once the unit of work completes |
| `IOutboundHttp` (given to plugins) | how a plugin calls out | the HTTP client factory's client |

The module registers the defaults first; an application's own registration wins. The defaults are right for one
server. On several, each server would count limits and claim slots alone, so such a host registers its own.

### PostgreSQL and EF Core

A plugin's contexts are EF Core on Npgsql, each in the plugin's schema, on the host's own database. One database is
what lets a plugin's call commit with the server's in one transaction. Servers coordinate through PostgreSQL advisory
locks (AbpCluster): one at a time installs system plugins at start, migrates a plugin, or runs a job. No other
database is planned.

### The tables in the application's context

The host's tables (`plugins`, `plugin_publishers`, `plugin_trust`, `plugin_files`, `plugin_manifests`,
`plugin_settings`) are mapped by the application's own context, through `IPluginsDbContext` and
`PluginsModel.ConfigurePlugins`, in that context's schema and migrations. The package has no context or migrations of
its own: an application keeps one migration history, and the application the host came from keeps its tables where
they are.

### Not now

- **Administration over HTTP, and pages.** `PluginManager` and `PluginTrust` are the API; the sample host maps them one
  way. A package of controllers can come when a second application wants the same routes.
- **Showing what plugins declare.** Health checks, web bundles, manifest sections and texts are kept for the
  application to show; the host doesn't run or serve them.
- **Purging a removed plugin's data.** Its schema stays until someone drops it.
- **Isolating plugin code for security.** A load context is for unloading and versions, not a sandbox. Signatures
  decide what runs.

## Consequences

- An ABP application on .NET 10 and PostgreSQL installs, upgrades and removes plugins at run time with one module, six
  tables and a blob store.
- The application the host came from moves onto the packages with its own names, so its packages, signatures and
  secrets stay valid.
- One process hosts one format and one profile. Tests that need another set it and set it back.
- A plugin built on `AbpPlugin.Sdk` runs on any host. One built on an application's own SDK runs only where that
  application's entry runner is.
- A host on several servers that keeps the defaults doesn't fail, but each server counts public limits and claims job
  slots alone: the site allows more calls than its limits say, and a job's occurrence may run on more than one server.
