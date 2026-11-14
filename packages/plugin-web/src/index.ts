// Plugins' web parts in the page: a Module Federation host that registers the remotes a server lists (AbpPlugin.Web's
// /api/plugins-web/remotes, or an application's own list) and loads what they expose. No framework of its own: the
// application names itself and passes the modules it shares with the plugins (its React, its design system), and
// ./react adds React's lazy components.

import { createInstance, type ModuleFederation } from '@module-federation/runtime';

/** A remote as a server lists it: its name, and its entry's address (absolute, or from the page's origin). */
export type PluginRemote = { name: string; entry: string };

/** What a host shares with the remotes: the federation runtime's own `shared` option. */
export type SharedModules = NonNullable<Parameters<typeof createInstance>[0]['shared']>;

export type PluginLoaderOptions = {
  /** The host's name in the federation. */
  name: string;
  /** The modules the host shares: plugins declare them with `import: false` and use the host's single instance. */
  shared: SharedModules;
  /** The federation instance; the runtime's by default (a test passes its own). */
  instance?: (options: Parameters<typeof createInstance>[0]) => Pick<ModuleFederation, 'registerRemotes' | 'loadRemote'>;
};

export type PluginLoader = {
  /** The federation instance, made on first use. */
  federation(): Pick<ModuleFederation, 'registerRemotes' | 'loadRemote'>;
  /** Registers each remote, or registers it again when its entry has changed (an upgrade), forgetting what it loaded. */
  register(remotes: Iterable<PluginRemote>, origin?: string): void;
  /** Loads `<remote>/<exposed>` once and keeps it; rejects when it can't be loaded or exports nothing. Not kept when it fails. */
  load<T = unknown>(remote: string, exposed: string): Promise<T>;
  /** Forgets one loaded module, so the next load tries again. */
  forget(remote: string, exposed: string): void;
  /** Hears each remote registered (again), to drop what was made from its modules. Returns a way to stop. */
  onChange(listener: (remote: string) => void): () => void;
};

export function createPluginLoader(options: PluginLoaderOptions): PluginLoader {
  let instance: ReturnType<PluginLoader['federation']> | null = null;
  /** remote name -> registered entry address */
  const registered = new Map<string, string>();
  /** "remote/exposed" -> the module, loading or loaded */
  const loading = new Map<string, Promise<unknown>>();
  const listeners = new Set<(remote: string) => void>();

  const federation = () => {
    instance ??= (options.instance ?? createInstance)({ name: options.name, remotes: [], shareStrategy: 'loaded-first', shared: options.shared });
    return instance;
  };

  return {
    federation,
    register(remotes, origin) {
      const base = origin ?? (typeof window !== 'undefined' ? window.location.origin : undefined);
      for (const { name, entry } of remotes) {
        const address = base ? new URL(entry, base).href : entry;
        const current = registered.get(name);
        if (current === address) continue;
        federation().registerRemotes([{ name, entry: address, type: 'module' }], { force: current !== undefined });
        registered.set(name, address);
        for (const id of [...loading.keys()]) if (id.startsWith(`${name}/`)) loading.delete(id);
        for (const listener of listeners) listener(name);
      }
    },
    load<T>(remote: string, exposed: string): Promise<T> {
      const id = `${remote}/${exposed}`;
      let promise = loading.get(id) as Promise<T> | undefined;
      if (!promise) {
        promise = federation()
          .loadRemote<{ default?: T }>(id)
          .then((mod) => {
            const value = mod && (mod.default ?? (mod as unknown as T));
            if (typeof value !== 'function' && (typeof value !== 'object' || value === null)) {
              throw new Error(`Module ${id} has no default export component.`);
            }
            return value as T;
          });
        const kept = promise;
        // A failed load isn't kept: the next one tries again.
        kept.catch(() => loading.get(id) === kept && loading.delete(id));
        loading.set(id, kept);
      }
      return promise;
    },
    forget(remote, exposed) {
      loading.delete(`${remote}/${exposed}`);
    },
    onChange(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
}

/** A plugin in AbpPlugin.Web's remotes list: its key, version and remote, and the sections the application adds. */
export type PluginWebEntry = { key: string; version: string; remote: PluginRemote; [section: string]: unknown };

/** The enabled plugins' remotes from AbpPlugin.Web's list, with the page's sign-in (its cookies). */
export async function fetchPluginRemotes(url = '/api/plugins-web/remotes', init?: RequestInit): Promise<PluginWebEntry[]> {
  const response = await fetch(url, { credentials: 'same-origin', ...init });
  if (!response.ok) throw new Error(`${url} answered ${response.status}`);
  return ((await response.json()) as { plugins: PluginWebEntry[] }).plugins;
}
