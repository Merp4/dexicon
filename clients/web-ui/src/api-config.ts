import type { CreateClientConfig } from './generated/client.gen';
import { getToken } from './token';

/**
 * Runtime configuration for the generated client.
 *
 * Kept out of the generated code because none of it is describable by an OpenAPI
 * document: the base URL is same-origin (the .NET host serves this SPA from its own
 * wwwroot), and the bearer token lives in sessionStorage rather than a cookie — no
 * cookie means no CSRF surface, and a token that dies with the tab.
 */
export const createClientConfig: CreateClientConfig = (config) => ({
  ...config,
  baseUrl: '',
  auth: () => getToken() ?? undefined,
});
