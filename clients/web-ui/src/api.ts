// Typed client for the Dexicon REST surface.
//
// The token lives in sessionStorage, not a cookie: no cookie means no CSRF surface,
// and a tab close is a sensible session boundary for a local tool.

const TOKEN_KEY = 'dexicon.token';

export function getToken(): string | null {
  try {
    return sessionStorage.getItem(TOKEN_KEY);
  } catch {
    return null; // private mode, blocked storage
  }
}

export function setToken(token: string | null) {
  try {
    if (token) sessionStorage.setItem(TOKEN_KEY, token);
    else sessionStorage.removeItem(TOKEN_KEY);
  } catch {
    /* non-fatal: the app still works for this page load */
  }
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly title: string,
    readonly detail?: string,
  ) {
    // Quote the server's own message. "Something went wrong" helps nobody.
    super(detail ? `${title} — ${detail}` : title);
  }
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = getToken();
  const headers = new Headers(init.headers);
  if (token) headers.set('Authorization', `Bearer ${token}`);
  if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');

  const res = await fetch(path, { ...init, headers });

  if (res.status === 204) return undefined as T;

  const text = await res.text();
  const body = text ? JSON.parse(text) : undefined;

  if (!res.ok) {
    throw new ApiError(res.status, body?.title ?? res.statusText, body?.detail);
  }
  return body as T;
}

// ── Types ───────────────────────────────────────────────────────────────────

export interface Corpus {
  id: string;
  name: string;
  description?: string;
  tenantId: string;
  owned: boolean;
  visibility: 'private' | 'shared';
  state: 'ready' | 'indexing' | 'degraded' | 'unavailable';
  embeddingModel: string;
  embeddingDimensions: number;
  chunkSize: number;
  chunkOverlap: number;
  boundaryMode: string;
  createdUtc: string;
  lastIndexedUtc?: string;
  sourceCount: number;
  fileCount: number;
  chunkCount: number;
  skippedCount: number;
  failedCount: number;
  sources: { id: string; kind: string; rootPath?: string; useGitignore: boolean; maxFileBytes: number }[];
}

export interface SearchHit {
  corpusId: string;
  corpusName?: string;
  filePath: string;
  language?: string;
  startLine: number;
  endLine: number;
  page?: number;
  section?: string;
  symbols: string[];
  content: string;
  score: number;
  location: string;
}

export interface SearchResult {
  query: string;
  mode: string;
  degraded: boolean;
  degradedReason?: string;
  scope: { id: string; name: string; state: string }[];
  hits: SearchHit[];
  tookMs: number;
  note?: string;
}

export interface Job {
  id: string;
  corpusId: string;
  kind: string;
  state: 'queued' | 'running' | 'succeeded' | 'failed' | 'degraded' | 'cancelled';
  phase?: string;
  filesTotal: number;
  filesDone: number;
  filesSkipped: number;
  filesFailed: number;
  chunksWritten: number;
  error?: string;
  startedUtc?: string;
  finishedUtc?: string;
}

export interface IndexedFile {
  id: string;
  relativePath: string;
  status: 'indexed' | 'skipped' | 'failed' | 'empty';
  statusDetail?: string;
  language?: string;
  sizeBytes: number;
  chunkCount: number;
  indexedUtc?: string;
}

export interface Health {
  status: string;
  qdrant: { reachable: boolean; endpoint: string };
  ollama: { reachable: boolean; endpoint: string; model: string; dimensions: number; error?: string };
  corpora: number;
  activeJob?: Job;
}

export interface WorkspaceListing {
  root: string;
  path: string;
  entries: { name: string; relativePath: string; isDirectory: boolean; childCount?: number }[];
}

export interface TokenSummary {
  id: string;
  name: string;
  tenantId: string;
  scopes: string;
  createdUtc: string;
  lastUsedUtc?: string;
  expiresUtc?: string;
  revokedUtc?: string;
}

export interface CreatedToken {
  token: TokenSummary;
  secret: string;
  mcpAddCommand: string;
}

export interface Tenant {
  id: string;
  displayName: string;
  createdUtc: string;
  disabled: boolean;
}

// ── Calls ───────────────────────────────────────────────────────────────────

export const api = {
  health: () => request<Health>('/healthz'),

  listCorpora: () => request<Corpus[]>('/api/corpora'),
  getCorpus: (nameOrId: string) => request<Corpus>(`/api/corpora/${encodeURIComponent(nameOrId)}`),
  createCorpus: (body: Record<string, unknown>) =>
    request<Corpus>('/api/corpora', { method: 'POST', body: JSON.stringify(body) }),
  updateCorpus: (nameOrId: string, body: Record<string, unknown>) =>
    request<Corpus>(`/api/corpora/${encodeURIComponent(nameOrId)}`, { method: 'PATCH', body: JSON.stringify(body) }),
  deleteCorpus: (nameOrId: string) =>
    request<void>(`/api/corpora/${encodeURIComponent(nameOrId)}`, { method: 'DELETE' }),
  reindex: (nameOrId: string, full = false) =>
    request<Job>(`/api/corpora/${encodeURIComponent(nameOrId)}/reindex?full=${full}`, { method: 'POST' }),
  listFiles: (nameOrId: string, status?: string) =>
    request<{ total: number; files: IndexedFile[] }>(
      `/api/corpora/${encodeURIComponent(nameOrId)}/files?limit=500${status ? `&status=${status}` : ''}`,
    ),

  search: (body: Record<string, unknown>) =>
    request<SearchResult>('/api/search', { method: 'POST', body: JSON.stringify(body) }),

  listJobs: (limit = 30) => request<Job[]>(`/api/jobs?limit=${limit}`),

  browse: (path?: string) =>
    request<WorkspaceListing>(`/api/workspaces${path ? `?path=${encodeURIComponent(path)}` : ''}`),

  listTenants: () => request<Tenant[]>('/api/tenants'),
  createTenant: (id: string, displayName?: string) =>
    request<Tenant>('/api/tenants', { method: 'POST', body: JSON.stringify({ id, displayName }) }),

  listTokens: () => request<TokenSummary[]>('/api/tokens'),
  createToken: (name: string, scopes: string[], expiresInDays?: number) =>
    request<CreatedToken>('/api/tokens', {
      method: 'POST',
      body: JSON.stringify({ name, scopes, expiresInDays }),
    }),
  revokeToken: (id: string) => request<void>(`/api/tokens/${encodeURIComponent(id)}`, { method: 'DELETE' }),
};

/**
 * Live indexing progress. EventSource cannot send an Authorization header, so this
 * uses fetch with a reader — which also gives us a clean abort.
 */
export function subscribeToProgress(
  onProgress: (p: Job & { currentFile?: string }) => void,
  onStateChange: (connected: boolean) => void,
): () => void {
  const controller = new AbortController();
  let stopped = false;

  (async () => {
    let backoff = 1000;
    while (!stopped) {
      try {
        const token = getToken();
        const res = await fetch('/api/events', {
          headers: token ? { Authorization: `Bearer ${token}` } : {},
          signal: controller.signal,
        });
        if (!res.ok || !res.body) throw new Error(`events: ${res.status}`);

        onStateChange(true);
        backoff = 1000;

        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (!stopped) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });

          let sep: number;
          while ((sep = buffer.indexOf('\n\n')) >= 0) {
            const frame = buffer.slice(0, sep);
            buffer = buffer.slice(sep + 2);
            const data = frame
              .split('\n')
              .filter((l) => l.startsWith('data: '))
              .map((l) => l.slice(6))
              .join('');
            if (data) {
              try {
                onProgress(JSON.parse(data));
              } catch {
                /* a malformed frame is not worth tearing the stream down for */
              }
            }
          }
        }
      } catch {
        if (stopped) return;
        onStateChange(false);
        await new Promise((r) => setTimeout(r, backoff));
        backoff = Math.min(backoff * 2, 15000);
      }
    }
  })();

  return () => {
    stopped = true;
    controller.abort();
  };
}
