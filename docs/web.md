# Plugins' web parts (AbpPlugin.Web)

A plugin can bring screens of its own: a browser bundle built as a
[Module Federation](https://module-federation.io) remote, which the application's pages load while they run. The
plugin's build packs it into the package's `web/` folder (`AbpPlugin.Build` takes `web/dist`), and its manifest names it:

```json
"web": { "remoteName": "acme_reports", "entry": "web/remoteEntry.js" }
```

`AbpPlugin.Web` serves those bundles and tells the page which remotes there are. Nothing is restarted: a plugin
installed, upgraded, disabled or removed shows in the next list the page asks for.

## In the application

```csharp
[DependsOn(typeof(AbpPluginModule), typeof(AbpPluginWebModule))]
public class MyAppModule : AbpModule { /* ... */ }

app.UseEndpoints(endpoints =>
{
    endpoints.MapPluginEndpoints();
    endpoints.MapPluginWeb().RequireAuthorization(); // the list for a signed-in person; the files to anybody
});
```

`MapPluginWeb` maps two things:

- **The files**, `GET /plugins-web/{key}/{folder}/{**file}`, from `{PluginsRoot}/{key}/{folder}/web/`. The folder is
  the one the version is installed in: the version, or the version and a suffix when that version was installed before
  and its folder is still kept. Only an enabled plugin's files are served, but every one of its installed versions', so
  a page opened before an upgrade still loads the chunks it asks for later. A former key (`PluginKeys`) reaches the
  plugin's files too. Nothing outside the `web/` folder is served. Each file goes with a content type by its extension
  and `Cache-Control: no-cache`, so a disabled plugin stops at once.
- **The remotes**, `GET /api/plugins-web/remotes`:

  ```json
  { "plugins": [ { "key": "acme.reports", "version": "1.2.0",
                   "remote": { "name": "acme_reports", "entry": "/plugins-web/acme.reports/1.2.0/remoteEntry.js" } } ] }
  ```

  Each enabled plugin with a web part, by key. The files are a plugin's code, public as any page's scripts are; the
  list says what is installed, so an application usually requires a sign-in for it, as above.

`PluginWebOptions` changes the prefix (`Prefix`), the package's folder (`Part`) and `CacheControl`.
`MapPluginPartFiles(prefix, part)` serves another folder of the plugins the same way, such as a phone app's bundles.

## What the application adds

The application decides what a plugin's screens are for: which of its pages a plugin adds to, its menu entries, who may
open them. It reads those from the manifest's own sections (`PluginHostProfile.ManifestSections`) and adds them to each
plugin in the list with an `IPluginWebSections`:

```csharp
public sealed class MyWebSections : IPluginWebSections
{
    public ValueTask<IReadOnlyDictionary<string, object?>?> ForAsync(PluginWebRemote remote, HttpContext http)
    {
        MyUiSection? ui = remote.Plugin.Manifest.Section<MyUiSection>("ui");
        // null leaves the plugin out of this person's list
        return ValueTask.FromResult<IReadOnlyDictionary<string, object?>?>(new Dictionary<string, object?> { ["pages"] = ui?.Pages ?? [] });
    }
}

context.Services.AddSingleton<IPluginWebSections, MyWebSections>();
```

`key`, `version` and `remote` are the list's own names. An application that builds its own list instead starts from
`PluginWebRemotes.Enabled()`.

## In the page

The page registers the listed remotes with Module Federation's runtime and loads what each exposes, sharing its own
React and design system with them. AbpPlugin.Web's browser package does that with no framework of its own, and a React
adapter beside it.
