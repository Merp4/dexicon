import { defineConfig } from '@hey-api/openapi-ts';

/**
 * Generates the API client from the OpenAPI document the server writes at build time.
 *
 * The input is a FILE, not a URL. Generating from a running instance would mean the
 * client could only be regenerated when something was up and authenticated, and would
 * quietly encode whatever that instance happened to be running. `Dexicon.json` is written
 * by `dotnet build`, so the client always matches the code it was generated from.
 *
 * This replaces hand-written types that had already drifted: chunk sets landed and
 * `api.ts` still described a Corpus with `chunkSize` and `embeddingModel` on it, fields
 * the server had moved onto a chunk set. Nothing failed — the UI simply read undefined.
 */
export default defineConfig({
  input: './Dexicon.json',
  output: {
    path: './src/generated',
    // No formatter: generated code is read in a diff, not edited, and a post-processor
    // is one more tool that has to be installed for the build to work at all.
  },
  plugins: [
    '@hey-api/typescript',
    {
      name: '@hey-api/sdk',
      // Throw on a non-2xx rather than returning a result union. The UI already has one
      // error path — ErrorBanner shows the server's own message — and it is the shape
      // every existing call site is written against.
      throwOnError: true,
    },
    {
      name: '@hey-api/client-fetch',
      // Configured at runtime in api.ts: the base URL is same-origin and the bearer token
      // comes from sessionStorage, neither of which belongs in generated code.
      runtimeConfigPath: './src/api-config.ts',
    },
  ],
});
