import type { CreateClientConfig } from './generated/client.gen';

/**
 * Runtime configuration for the generated client.
 *
 * Kept out of the generated code because none of it is describable by an OpenAPI
 * document: the base URL is same-origin, because the .NET host serves this SPA from its
 * own wwwroot.
 *
 * The token is NOT here. This used to set `auth: () => getToken()`, which never ran: the
 * client only resolves `auth` for operations the OpenAPI document marks as secured, and
 * the document declared no security schemes at all. Every generated request went out with
 * no Authorization header and came back 401, which the UI reported as a bad token. It is
 * attached by a request interceptor in api.ts instead, which holds whatever the document
 * happens to say.
 */
export const createClientConfig: CreateClientConfig = (config) => ({
  ...config,
  baseUrl: '',
});
