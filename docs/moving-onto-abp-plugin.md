# Moving an application onto AbpPlugin

For Toda, the application the plugin host came from. It had the host built in, and has packages out in its own format,
signed by its publishers; its plugins' secrets, schemas and the host's tables are in its database; its plugins are
built on its own SDK with its own build package and signing tool. All of that stays. Done when Toda runs on the
AbpPlugin packages with no change to its packages, signatures, secrets, schemas or tables, and a plugin built before
the move installs, upgrades and runs after it as before.

The steps hold for any application with packages already out.

1. **Packages.** Toda references `AbpPlugin`, `AbpPlugin.Sdk` and `AbpPlugin.Packaging` from nuget.org, at one
   version, in place of its own copies of the host. The types keep their names and namespaces (`AbpPlugin`,
   `AbpPlugin.Hosting`, `AbpPlugin.Packaging`), so its code that calls them doesn't change. Its own copies go once
   nothing references them.

2. **Its format.** Its packages carry its own file names and its signatures are made over its own prefix, so it says
   so once, before any package is read or signed: a module initializer in the assembly that sets its profile.

   ```csharp
   PluginPackageFormat.Current = new PluginPackageFormat
   {
       Manifest = "toda-plugin.json",
       FormerManifests = [FormerManifest],
       Signature = "toda-signature.json",
       Marker = ".toda-package",
       FormerMarkers = [FormerMarker],
       SignedPrefix = "toda-plugin-signature/1\n",
       PackageExtensions = [".zip", ".todapkg", FormerPackageExtension],
   };
   ```

   The former names are those its packages, marks and uploads had before it took the name Toda, kept as constants of
   its own. Check each name and the prefix against the code they replace, character for character: a signed prefix
   that differs makes every signature already out fail, and a manifest name that differs makes every package look
   empty.

3. **Its profile.** Toda sets `PluginHostProfile.Current` already, in a module initializer. What each part keeps:

   - `ProductName`: "Toda", as the host's messages name it ("only Toda's release key signs it").
   - `SecretsPurpose`: the Data Protection purpose its plugins' secrets were protected under before. It isn't
     AbpPlugin's default, and left at the default every secret a plugin stored reads as null.
   - `SettingPlaces`: where its operators set a host setting besides configuration (its chart's values), said after the
     setting's key in a message.
   - `FormerKeys` and `FormerModules`: its plugins' one-word keys from before app IDs and their app IDs, and its
     modules' former names, so an old package is told what to install and an old `requires` still finds its module.
   - `PinnedSchemas`: the plugins that keep the schemas they had before app IDs, for good.
   - `EntryRunners`: its SDK's plugin base class, run through an `IPluginEntryRunner` that registers through
     `PluginServiceCollection`'s methods by type (`AddDbContext(type, useSchema)`, `AddSeeder`, `AddEventHandler`,
     `AddRecurringJob`, `AddExtension`, `AddEndpoint`, `AddHealthCheck`, `AddPageAccess`).
   - `ManifestSections`: its manifests' own sections (its `ui` and `mobile`, among others), checked as a manifest loads.
   - `SharedAssemblyPrefixes`, `FormerAssemblies`, `MovedType`, `FallsBackAttribute` and `FormerNamesRefusal`: the
     assemblies it shares with plugins, the names they had before, the types that moved, its SDK's attribute for
     plugins that fall back on older servers, and its own words for a plugin built against former names.
   - `TransactionalMark` and `JoinUnitsOfWork`: its SDK's mark for work that isn't one unit of work, and the switch
     that joins its modules' work to a plugin call's unit of work.
   - `ReleaseKeys`: the release keys built into it.
   - `IsOwn`: its own publishers (`toda.*`), whose packages only a release key signs.

4. **Tables.** Its platform's context implements `IPluginsDbContext`, calls `PluginsModel.ConfigurePlugins` in the
   schema the tables are in now, and replaces the host's context (`ReplaceDbContext`). The package maps the same tables
   by the same names (`plugins`, `plugin_publishers`, `plugin_trust`, `plugin_files`, `plugin_manifests`,
   `plugin_settings`). Add a migration after the switch: it should be empty. Anything in it is a difference to look at
   before it ships, not to accept.

5. **Seams.** It registers its own, in its module's `ConfigureServices`, which runs after `AbpPluginModule`'s:

   | Seam | Toda's |
   |---|---|
   | `IPluginCaller` | the account its requests and jobs act for |
   | `IPluginCallLimits` | its site limits, counted for all its servers |
   | `IPluginInstallCheck` | its checks that plugins' settings sections and pages' keys don't clash |
   | `IPluginChanges` | the notice that makes open pages reload plugins |
   | `IPluginJobSlots` | its claimed slots, which all its servers see |

   Each replaces a default for one server; check after the move that the container gives Toda's, not the default.

6. **Services plugins get.** It exposes the host services its plugins use with `PluginHostServiceOptions.Expose`: its
   event publisher (over its outbox), its outgoing HTTP client with its rules for private addresses, its account
   lookup, its other services for plugins. An `IEventPublisher` or `IOutboundHttp` it exposes takes the place of the
   host's fallback. Plugins get those and nothing else.

7. **Wiring.** What the host keeps and Toda shows stays Toda's: its plugin pages call `PluginManager` and `PluginTrust`;
   its health page runs plugins' checks; its front end loads their web bundles; its settings API reads
   `PluginSettingsStore`; its events reach plugins through `PluginEventDelivery`. Its start calls
   `PluginManager.StartAsync` after its migrations, as before; the reconciler and the job scheduler come with the
   module.

8. **Configuration and locks.** The `PluginHost:*` settings keep their names. `AbpClusterOptions.KeyPrefix` stays what
   its servers use now, so during a rolling upgrade old and new servers take the same locks.

9. **Its build package and signing tool.** Plugin authors keep `Toda.Plugin.Build` and `toda-plugin-sign`.
   `Toda.Plugin.Build` carries `AbpPlugin.Build`'s props and targets and sets its names over them:

   | Property | Toda's |
   |---|---|
   | `AbpPluginManifestFile`, `AbpPluginManifestTemplate` | its manifest's name, and its template's |
   | `AbpPluginSignCommand` | `toda-plugin-sign` |
   | `AbpPluginMoreHostAssemblies` | its SDK and contracts (`Toda.Sdk.dll;Toda.*.Contracts.dll`) |
   | `AbpPluginDisplayName`, `AbpPluginZipHint` | "Toda plugin", and where to upload the zip |
   | `AbpPluginPropertyPrefix` | `TodaPlugin`, so errors name the `TodaPlugin*` properties its authors set |

   Its `TodaPlugin*` properties map to the `AbpPlugin*` ones before the targets are imported. Its signing tool is a
   small program of its own, packed as a .NET tool with `ToolCommandName` `toda-plugin-sign`: it sets its format, then
   runs the same commands as `abp-plugin-sign`, so a package it signs is one its servers read.

   ```csharp
   PluginPackageFormat.Current = TodaPackageFormat.Format;   // the format of step 2
   return PluginSigningTool.Run(args, "toda-plugin-sign");
   ```

   Run its `verify` on a package signed before the move: it must say `good`.

Pin it with its existing tests, which must pass unchanged before and after, and three more: a package signed before the
move installs and upgrades after it; a secret a plugin stored before the move reads the same after it; a system plugin
from its image loads as before.
