import * as React from 'react';
import * as ReactDOM from 'react-dom';
import { Component, StrictMode, Suspense, useEffect, useState, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import { createPluginLoader, fetchPluginRemotes, type PluginWebEntry } from '@abpjs/plugin-web';
import { reactPluginComponents } from '@abpjs/plugin-web/react';

// The sample host's page: each enabled plugin's ./Page, from the remotes AbpPlugin.Web lists. The page shares its React
// with the plugins, so each renders with this one.
const loader = createPluginLoader({
  name: 'sample_host',
  shared: {
    react: { version: React.version, lib: () => React, shareConfig: { singleton: true, requiredVersion: '^19.0.0' } },
    'react-dom': { version: ReactDOM.version, lib: () => ReactDOM, shareConfig: { singleton: true, requiredVersion: '^19.0.0' } },
  },
});
const components = reactPluginComponents(loader);

class Failed extends Component<{ plugin: string; children: ReactNode }, { error: string | null }> {
  state = { error: null as string | null };
  static getDerivedStateFromError(error: Error) {
    return { error: error.message };
  }
  render() {
    return this.state.error ? <p role="alert">{this.props.plugin}'s page couldn't be loaded: {this.state.error}</p> : this.props.children;
  }
}

function Plugins() {
  const [plugins, setPlugins] = useState<PluginWebEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    fetchPluginRemotes()
      .then((list) => {
        loader.register(list.map((p) => p.remote));
        setPlugins(list);
      })
      .catch((e: Error) => setError(e.message));
  }, []);

  if (error) {
    return (
      <p>
        {error}. <a href="/sample/sign-in">Sign in as the sample's admin</a>
      </p>
    );
  }
  if (!plugins) return <p>Loading…</p>;
  if (!plugins.length) return <p>No plugin with a web part is enabled.</p>;
  return (
    <>
      {plugins.map((plugin) => {
        const Page = components.lazy(plugin.remote.name, 'Page');
        return (
          <Failed key={plugin.key} plugin={plugin.key}>
            <Suspense fallback={<p>Loading {String(plugin.name ?? plugin.key)}…</p>}>
              <Page />
            </Suspense>
          </Failed>
        );
      })}
    </>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <h1>AbpPlugin sample host</h1>
    <Plugins />
  </StrictMode>,
);
