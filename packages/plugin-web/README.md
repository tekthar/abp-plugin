# @abpjs/plugin-web

Plugins' web parts in the page, for an ABP application that hosts plugins with
[AbpPlugin](https://github.com/tekthar/abp-plugin). The server's `AbpPlugin.Web` serves each enabled plugin's browser
bundle (a [Module Federation](https://module-federation.io) remote) and lists the remotes; this package registers them
and loads what they expose, sharing the application's own modules with the plugins. It has no framework of its own;
`@abpjs/plugin-web/react` adds React's lazy components.

> **Preview.** 0.x versions may change their API in any minor version. Pin an exact version.

```ts
import * as React from 'react';
import * as ReactDOM from 'react-dom';
import { createPluginLoader, fetchPluginRemotes } from '@abpjs/plugin-web';
import { reactPluginComponents } from '@abpjs/plugin-web/react';

// The modules the page shares: plugins declare them with `import: false` and render with the page's one copy.
const loader = createPluginLoader({
  name: 'my_app',
  shared: {
    react: { version: React.version, lib: () => React, shareConfig: { singleton: true, requiredVersion: '^19.0.0' } },
    'react-dom': { version: ReactDOM.version, lib: () => ReactDOM, shareConfig: { singleton: true, requiredVersion: '^19.0.0' } },
  },
});
const components = reactPluginComponents(loader);

const plugins = await fetchPluginRemotes(); // GET /api/plugins-web/remotes, with the page's sign-in
loader.register(plugins.map((p) => p.remote)); // again after an upgrade: a changed entry is registered anew
const Page = components.lazy(plugins[0].remote.name, 'Page'); // <Suspense><Page /></Suspense>
```

- `createPluginLoader({ name, shared })`: `register(remotes)`, `load(remote, exposed)` (loaded once and kept; a failed
  load isn't), `forget(remote, exposed)`, `onChange(listener)`, `federation()`.
- `reactPluginComponents(loader)`: `load`, `lazy` (the same component each time until its remote changes), `forget`.
- `fetchPluginRemotes(url?, init?)`: the server's list, each plugin with its `key`, `version`, `remote` and the sections
  the application adds.

The server side, and how a plugin builds its web part: [docs/web.md](https://github.com/tekthar/abp-plugin/blob/main/docs/web.md).
`samples/AbpPlugin.SampleHost/web` is a page that uses it.

Apache-2.0. Not a Volosoft project: ABP Framework is made and owned by Volosoft, and this package isn't affiliated with
or endorsed by them.
