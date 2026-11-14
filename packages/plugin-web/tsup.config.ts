import { defineConfig } from 'tsup';

// The loader and its React adapter, each an ESM entry with its types. React and the federation runtime stay outside:
// the application brings React, and the runtime is a dependency.
export default defineConfig({
  entry: ['src/index.ts', 'src/react.ts'],
  format: ['esm'],
  dts: true,
  sourcemap: true,
  clean: true,
  target: 'es2022',
  external: ['react', '@module-federation/runtime'],
});
