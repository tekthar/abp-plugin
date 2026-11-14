import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { federation } from '@module-federation/vite';

// The sample plugin's web part, a Module Federation remote. The host serves dist/ from the version installed, at
// /plugins-web/acme.abp-sample/{folder}/, so every address in the bundle is relative (base './'). React comes from the
// page: `import: false` ships no copy of it, so the plugin renders with the page's one React.
const fromPage = (requiredVersion: string) => ({ singleton: true, requiredVersion, import: false as const });

export default defineConfig({
  base: './',
  plugins: [
    react(),
    federation({
      name: 'acme_abp_sample',
      filename: 'remoteEntry.js',
      exposes: {
        // The sample host's page shows each plugin's ./Page.
        './Page': './src/Page.tsx',
      },
      shared: {
        react: fromPage('^19.0.0'),
        'react-dom': fromPage('^19.0.0'),
      },
      dts: false,
      manifest: true,
    }),
  ],
  build: { target: 'es2022', outDir: 'dist', emptyOutDir: true, modulePreload: false },
});
