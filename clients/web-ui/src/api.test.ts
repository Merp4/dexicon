import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api, setToken } from './api';
import { client } from './generated/client.gen';

/**
 * What the app does with a request that fails.
 *
 * This exists because of a bug that took the whole UI down. The generator was told
 * `throwOnError: true` and silently did not apply it — the generated SDK still defaults
 * to `ThrowOnError = false` — so a failed request came back as `{ data: undefined }`
 * rather than throwing. The facade returned that `undefined`, a component stored it in
 * state, and the next `.map` threw "Cannot read properties of undefined". A blank page,
 * on every load, while ErrorBanner sat there unused.
 *
 * Nothing in the type system objected: `data` is optional, and `data as T` erases it.
 * So the guarantee is asserted here instead.
 */
const problem = (status: number, title: string, detail: string) =>
  new Response(JSON.stringify({ type: 'about:blank', title, detail, status }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });

let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  fetchMock = vi.fn();
  client.setConfig({
    ...client.getConfig(),
    // The client's own `fetch`, not the global: it resolves `globalThis.fetch` when the
    // client is constructed, so a global stub installed later never reaches it.
    fetch: fetchMock as unknown as typeof fetch,
    // The app runs same-origin and sends `/api/...`. Outside a browser `new Request()` is
    // undici's, which rejects a relative URL, so the tests need an origin to resolve
    // against. Nothing else about the request changes.
    baseUrl: 'http://dexicon.test',
  });
});

afterEach(() => {
  setToken(null);
  client.setConfig({ ...client.getConfig(), fetch: undefined, baseUrl: '' });
});

describe('every request', () => {
  it('carries the bearer token', async () => {
    // The regression this file was written for. The client's `auth` option is only
    // consulted for operations the OpenAPI document marks as secured, and the document
    // declares no security schemes — so `auth` never ran, every request went out
    // anonymous, and the UI told people their token was wrong.
    setToken('dex_test-token');
    fetchMock.mockResolvedValue(new Response('[]', {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }));

    await api.listCorpora();

    const request = fetchMock.mock.calls[0][0] as Request;
    expect(request.headers.get('Authorization')).toBe('Bearer dex_test-token');
  });

  it('sends no Authorization header when there is no token', async () => {
    // An empty bearer is not the same as none, and the server answers them differently.
    fetchMock.mockResolvedValue(new Response('[]', {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }));

    await api.listCorpora();

    expect((fetchMock.mock.calls[0][0] as Request).headers.has('Authorization')).toBe(false);
  });
});

describe('a failing request', () => {
  it('rejects rather than resolving with undefined', async () => {
    fetchMock.mockResolvedValue(problem(401, 'Missing credentials', 'Provide a token.'));

    await expect(api.listCorpora()).rejects.toBeInstanceOf(ApiError);
  });

  it('carries the server’s own message, which is written for a person to act on', async () => {
    // "Unknown corpus 'api'. Visible corpora: api-repo, rfc-library." is the most useful
    // thing in the response; a generic "Request failed" throws it away.
    fetchMock.mockResolvedValue(
      problem(404, 'Unknown corpus', "Unknown corpus 'api'. Visible corpora: api-repo."),
    );

    await expect(api.getCorpus('api')).rejects.toMatchObject({
      status: 404,
      title: 'Unknown corpus',
      detail: "Unknown corpus 'api'. Visible corpora: api-repo.",
    });
  });

  it('reports a status even when the body is not problem details', async () => {
    // A proxy or a crash can answer with anything at all.
    fetchMock.mockResolvedValue(new Response('<html>502</html>', { status: 502 }));

    await expect(api.listJobs()).rejects.toMatchObject({ status: 502 });
  });

  it('reports a network failure rather than swallowing it', async () => {
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'));

    await expect(api.listCorpora()).rejects.toBeInstanceOf(ApiError);
  });
});

describe('a successful request', () => {
  it('resolves with the body', async () => {
    fetchMock.mockResolvedValue(
      new Response(JSON.stringify([{ id: 'c1', name: 'docs' }]), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );

    await expect(api.listCorpora()).resolves.toEqual([{ id: 'c1', name: 'docs' }]);
  });

  it('accepts a 204 with no body, because deletes answer that way', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 204 }));

    // null, not undefined: the client parses an empty body to null. Either way nothing
    // threw, which is what the delete call sites rely on.
    await expect(api.deleteCorpus('docs')).resolves.toBeNull();
  });
});
