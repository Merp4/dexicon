import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    // The .NET host serves this from wwwroot; Dockerfile copies dist/ there.
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
  },
  server: {
    port: 5180,
    // `npm run dev` talks to the locally-running API (scripts/dev.ps1).
    proxy: {
      '/api': 'http://127.0.0.1:8477',
      '/healthz': 'http://127.0.0.1:8477',
      '/mcp': 'http://127.0.0.1:8477',
    },
  },
});
