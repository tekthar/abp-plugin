// React on the plugin loader: a plugin's exposed component, loaded once and kept, as a value or as a React.lazy
// component with a stable identity, dropped when its remote is registered again (an upgrade).

import * as React from 'react';
import type { ComponentType, LazyExoticComponent } from 'react';
import type { PluginLoader } from './index';

export type AnyComponent = ComponentType<Record<string, unknown>>;

export type ReactPluginComponents = {
  /** Loads `<remote>/<Component>` once and keeps it; rejects when the bundle can't be loaded or exports no component. */
  load(remote: string, component: string): Promise<AnyComponent>;
  /** A React.lazy component for `<remote>/<Component>`, the same one each time until its remote changes. */
  lazy(remote: string, component: string): LazyExoticComponent<AnyComponent>;
  /** Forgets a component, so a retry loads it again. */
  forget(remote: string, component: string): void;
};

export function reactPluginComponents(loader: PluginLoader): ReactPluginComponents {
  /** "remote/Component" -> lazy component */
  const lazies = new Map<string, LazyExoticComponent<AnyComponent>>();
  loader.onChange((remote) => {
    for (const id of [...lazies.keys()]) if (id.startsWith(`${remote}/`)) lazies.delete(id);
  });

  const load = (remote: string, component: string) => loader.load<AnyComponent>(remote, component);
  return {
    load,
    lazy(remote, component) {
      const id = `${remote}/${component}`;
      let lazy = lazies.get(id);
      if (!lazy) {
        lazy = React.lazy(async () => ({ default: await load(remote, component) }));
        lazies.set(id, lazy);
      }
      return lazy;
    },
    forget(remote, component) {
      lazies.delete(`${remote}/${component}`);
      loader.forget(remote, component);
    },
  };
}
