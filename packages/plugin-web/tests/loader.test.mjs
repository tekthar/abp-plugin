// The loader as built: it imports only the federation runtime and React, and registers, loads and forgets what the
// remotes expose. `npm test` builds it first.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { createPluginLoader, fetchPluginRemotes } from '../dist/index.js';
import { reactPluginComponents } from '../dist/react.js';

const folder = new URL('../src/', import.meta.url);

function fake(modules) {
  const calls = { registered: [], loaded: [] };
  const instance = (options) => {
    calls.options = options;
    return {
      registerRemotes: (remotes, how) => calls.registered.push({ remotes, how }),
      loadRemote: async (id) => {
        calls.loaded.push(id);
        if (!(id in modules)) throw new Error(`no ${id}`);
        return modules[id];
      },
    };
  };
  return { calls, instance };
}

test('it imports only the federation runtime and React', () => {
  for (const file of readdirSync(folder)) {
    const source = readFileSync(new URL(file, folder), 'utf8');
    for (const [, from] of source.matchAll(/from\s+'([^']+)'/g)) {
      assert.ok(['@module-federation/runtime', 'react', './index'].includes(from), `${file} imports ${from}`);
    }
  }
});

test('a remote is registered once, again with force when its entry changes, and its loads are forgotten', async () => {
  const Card = () => null;
  const { calls, instance } = fake({ 'acme/Card': { default: Card } });
  const loader = createPluginLoader({ name: 'host', shared: {}, instance });
  const changed = [];
  loader.onChange((remote) => changed.push(remote));

  loader.register([{ name: 'acme', entry: '/plugins-web/acme.reports/1.0.0/remoteEntry.js' }], 'http://site.test');
  loader.register([{ name: 'acme', entry: '/plugins-web/acme.reports/1.0.0/remoteEntry.js' }], 'http://site.test');
  assert.equal(calls.options.name, 'host');
  assert.equal(calls.options.shareStrategy, 'loaded-first');
  assert.deepEqual(calls.registered, [
    { remotes: [{ name: 'acme', entry: 'http://site.test/plugins-web/acme.reports/1.0.0/remoteEntry.js', type: 'module' }], how: { force: false } },
  ]);

  assert.equal(await loader.load('acme', 'Card'), Card);
  assert.equal(await loader.load('acme', 'Card'), Card);
  assert.deepEqual(calls.loaded, ['acme/Card'], 'loaded once and kept');

  loader.register([{ name: 'acme', entry: '/plugins-web/acme.reports/1.1.0/remoteEntry.js' }], 'http://site.test');
  assert.equal(calls.registered[1].how.force, true, 'an upgrade registers it again');
  assert.deepEqual(changed, ['acme', 'acme']);
  await loader.load('acme', 'Card');
  assert.deepEqual(calls.loaded, ['acme/Card', 'acme/Card'], 'loaded again from the new entry');
});

test('a failed load is not kept, and a module without a component is refused', async () => {
  const { calls, instance } = fake({ 'acme/Empty': null, 'acme/Number': 5, 'acme/Bare': () => null });
  const loader = createPluginLoader({ name: 'host', shared: {}, instance });
  await assert.rejects(loader.load('acme', 'Missing'), /no acme\/Missing/);
  await assert.rejects(loader.load('acme', 'Missing'));
  assert.equal(calls.loaded.filter((id) => id === 'acme/Missing').length, 2, 'the next load tries again');
  await assert.rejects(loader.load('acme', 'Empty'), /has no default export component/);
  await assert.rejects(loader.load('acme', 'Number'), /has no default export component/);
  assert.equal(typeof (await loader.load('acme', 'Bare')), 'function', 'a module that is the component itself');
});

test('the React adapter keeps one lazy component per entry until its remote changes', () => {
  const { instance } = fake({});
  const loader = createPluginLoader({ name: 'host', shared: {}, instance });
  const components = reactPluginComponents(loader);
  loader.register([{ name: 'acme', entry: 'http://site.test/a/remoteEntry.js' }]);
  const first = components.lazy('acme', 'Card');
  assert.equal(components.lazy('acme', 'Card'), first);
  components.forget('acme', 'Card');
  const second = components.lazy('acme', 'Card');
  assert.notEqual(second, first, 'forgotten, a retry gets a new one');
  loader.register([{ name: 'acme', entry: 'http://site.test/b/remoteEntry.js' }]);
  assert.notEqual(components.lazy('acme', 'Card'), second, 'a new entry, a new component');
});

test("the remotes list is read with the page's sign-in, and a refusal is an error", async () => {
  const asked = [];
  globalThis.fetch = async (url, init) => {
    asked.push([url, init.credentials]);
    if (url === '/denied') return { ok: false, status: 401 };
    return { ok: true, json: async () => ({ plugins: [{ key: 'acme.reports', version: '1.0.0', remote: { name: 'acme', entry: '/e.js' }, pages: [] }] }) };
  };
  const plugins = await fetchPluginRemotes();
  assert.deepEqual(plugins.map((p) => [p.key, p.remote.name, Array.isArray(p.pages)]), [['acme.reports', 'acme', true]]);
  assert.deepEqual(asked[0], ['/api/plugins-web/remotes', 'same-origin']);
  await assert.rejects(fetchPluginRemotes('/denied'), /answered 401/);
});
