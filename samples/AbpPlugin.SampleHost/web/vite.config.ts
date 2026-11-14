import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The sample host's page, built into the host's wwwroot, which it serves at /.
export default defineConfig({
  plugins: [react()],
  build: { target: 'es2022', outDir: '../wwwroot', emptyOutDir: true },
});
