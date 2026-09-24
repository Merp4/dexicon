/// <reference types="vitest/config" />
import { defineConfig, searchForWorkspaceRoot } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { fileURLToPath } from 'node:url';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    // `@/` is the shadcn convention; generated components import through it.
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  build: {
    // The .NET host serves this from wwwroot; Dockerfile copies dist/ there.
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
  },
  test: {
    // jsdom rather than a real browser: these tests assert what the component renders,
    // not how a browser paints it. A headless browser would be the right tool for
    // "is this actually visible", and is a different, slower kind of test.
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    // Generated code is not ours to cover.
    coverage: { exclude: ['src/generated/**', '**/*.config.ts'] },
  },
  server: {
    port: 5180,
    fs: {
      // The default, plus the one server directory a test reads: terms.test.ts holds
      // lib/terms.ts to SparseEncoder.cs, which lives outside this package.
      allow: [searchForWorkspaceRoot(process.cwd()), '../../src/Dexicon.Core/Search'],
    },
    // `npm run dev` talks to the locally-running API (scripts/dev.ps1).
    proxy: {
      '/api': 'http://127.0.0.1:8477',
      '/healthz': 'http://127.0.0.1:8477',
      '/mcp': 'http://127.0.0.1:8477',
    },
  },
});
