/**
 * The app's view of the API.
 *
 * Every type here is GENERATED from the OpenAPI document the server writes at build time
 * (`npm run generate`). The hand-written versions that used to live in this file had
 * already drifted: chunk sets landed and `Corpus` still declared `chunkSize` and
 * `embeddingModel`, fields the server had moved onto a chunk set. Nothing failed; the UI
 * read `undefined` and rendered it.
 *
 * Request bodies are typed from the generated request types too, not just responses.
 * They were `Record<string, unknown>` with a cast for a while, because the document
 * marked every field of every body as required, because positional record parameters
 * with no default are `required` in the schema, so the generated types demanded fields the API
 * does not. The contracts carry defaults now, so the document says what is actually
 * optional and a misspelt or mistyped field is a compile error again.
 *
 * What is still hand-written, and why:
 *   - the `api` facade below, so call sites read as `api.listCorpora()` rather than
 *     `getApiCorpora({ throwOnError: true })`, and so a URL shape change stays here
 *   - `ApiError`, because the generated client throws its own error shape and the UI has
 *     one error path that shows the server's message verbatim
 *   - `subscribeToProgress`, because server-sent events are a stream, not an operation an
 *     OpenAPI document can describe usefully
 */
import {
  deleteApiCorporaByNameOrId,
  deleteApiCorporaByNameOrIdChunkSetsBySetName,
  deleteApiCorporaByNameOrIdDocumentsByFileId,
  deleteApiCorporaByNameOrIdSourcesBySourceId,
  deleteApiEmbeddingModelsByModel,
  deleteApiTokensById,
  getApiCorpora,
  getApiCorporaByNameOrId,
  getApiCorporaByNameOrIdChunkSets,
  getApiCorporaByNameOrIdFile,
  getApiCorporaByNameOrIdCoverage,
  patchApiCorporaByNameOrIdSourcesBySourceId,
  getApiCorporaByNameOrIdFiles,
  getApiDocuments,
  getApiDocumentsBySha256Text,
  postApiSession,
  deleteApiSession,
  putApiTokensByIdCorpora,
  putApiTokensByIdScopes,
  getApiEmbeddingModels,
  getApiEmbeddingProviders,
  getApiJobs,
  getApiProposals,
  getApiTokens,
  getApiWorkspaces,
  getApiWorkspacesGit,
  getHealthz,
  patchApiCorporaByNameOrId,
  patchApiCorporaByNameOrIdChunkSetsBySetName,
  postApiCorpora,
  postApiCorporaByNameOrIdChunkSets,
  postApiCorporaByNameOrIdChunkSetsBySetNamePromote,
  postApiCorporaByNameOrIdDocumentsAttach,
  postApiCorporaByNameOrIdReindex,
  postApiCorporaByNameOrIdSources,
  postApiEmbeddingModelsProbe,
  postApiProposalsByIdApprove,
  postApiProposalsByIdReject,
  postApiSearch,
  postApiTokens,
  putApiEmbeddingModelsProfile,
} from './generated';
import type {
  AddSourceRequest,
  UpdateSourceRequest,
  AttachDocumentRequest,
  CreateChunkSetRequest,
  CreateCorpusRequest,
  CreateTokenRequest,
  SignInRequest,
  UpdateTokenCorporaRequest,
  UpdateTokenScopesRequest,
  JobSummary,
  ProbeModelRequest,
  SaveModelProfileRequest,
  SearchApiRequest,
  UpdateChunkSetRequest,
  UpdateCorpusRequest,
  UploadResponse,
} from './generated';
import { client } from './generated/client.gen';
import { getToken } from './token';

/**
 * The bearer token, on every generated request.
 *
 * An interceptor rather than the client's `auth` option. `auth` is only consulted for
 * operations the OpenAPI document marks as secured, and the document declared no security
 * schemes at all, so it was never called, every request went out anonymous, the server
 * answered 401 "Missing credentials", and the UI told people their token was wrong.
 *
 * The document now declares the scheme (see the transformer in Program.cs), so `auth`
 * would work too. This stays because it does not depend on that: whether the header is
 * attached should not be a property of a generated file. The client skips its own auth
 * step when the header is already set, so the two do not fight.
 *
 * Read per request, not captured: the token arrives after this module is imported. A
 * header the caller set is left alone, which is how `signOut` sends the session it ends.
 */
client.interceptors.request.use((request) => {
  const token = getToken();
  if (token && !request.headers.has('Authorization')) request.headers.set('Authorization', `Bearer ${token}`);
  return request;
});

export { getToken, setToken } from './token';

// ── Types ───────────────────────────────────────────────────────────────────
//
// Aliased to the names the app already uses. The server's vocabulary is
// `CorpusSummary`; the UI's is `Corpus`, and renaming every call site to match a
// generator's convention would be churn for nothing.

export type {
  ChunkSetSummary as ChunkSet,
  CorpusSummary as Corpus,
  CorpusDefaults,
  CoverageGap,
  CoverageReport,
  SourceSummary as CorpusSource,
  CreatedTokenResponse as CreatedToken,
  EmbeddingModelInfo,
  EmbeddingProviderInfo,
  ExtractedTextResponse as DocumentText,
  FileSummary as IndexedFile,
  HealthResponse as Health,
  IndexedFileText,
  JobSummary as Job,
  ProposalView as Proposal,
  LibraryAttachment,
  LibraryDocument,
  ModelCapabilities,
  SearchHit,
  SearchResult,
  TokenSummary,
  UploadFailure,
  UploadResponse,
  WorkspaceListing,
  GitRef,
  GitRefListing,
  GitRefsResponse,
  GitTracking,
  GitUpstream,
} from './generated';

/**
 * Model pull progress. Hand-written because it arrives as server-sent events rather than
 * a response body, so the OpenAPI document describes the request and nothing else.
 */
export interface ModelPullEvent {
  model: string;
  status?: string;
  completed?: number;
  total?: number;
  percent?: number;
  done?: boolean;
  error?: string;
}

// ── Errors ──────────────────────────────────────────────────────────────────

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly title: string,
    readonly detail?: string,
  ) {
    // Quote the server's own message. "Something went wrong" helps nobody.
    super(detail ? `${title}: ${detail}` : title);
  }
}

/**
 * Turns whatever the generated client threw into the one error shape the UI renders.
 *
 * The server answers failures with RFC 9457 problem details and writes them for a person
 * to act on: "Unknown corpus 'api'. Visible corpora: api-repo, rfc-library." Losing that
 * to a generic message would throw away the most useful thing in the response.
 */
function toApiError(e: unknown, status = 0): ApiError {
  if (e instanceof ApiError) return e;

  const err = e as { status?: number; title?: string; detail?: string; message?: string };
  const problem = (e as { error?: { title?: string; detail?: string; status?: number } }).error;

  return new ApiError(
    problem?.status ?? err.status ?? status,
    problem?.title ?? err.title ?? err.message ?? 'Request failed',
    problem?.detail ?? err.detail,
  );
}

/**
 * Unwraps the generated client's envelope, and normalises its errors.
 *
 * The envelope is checked here rather than relying on the generator's `throwOnError`.
 * That option was set and had no effect, because the generated SDK still defaults to
 * `ThrowOnError = false`, so every failed request returned `{ data: undefined }` and
 * this function handed that `undefined` straight into component state. The next `.map`
 * took the whole app down with "Cannot read properties of undefined", and ErrorBanner,
 * which exists precisely to show the server's message, never ran.
 *
 * Reading the envelope works whether the client throws or not, so it cannot regress on a
 * config flag again.
 */
async function call<T>(op: () => Promise<Envelope<T>>): Promise<T> {
  let result: Envelope<T>;
  try {
    result = await op();
  } catch (e) {
    throw toApiError(e); // the network never got there, or the client threw
  }

  if (result.error !== undefined || (result.response && !result.response.ok)) {
    throw toApiError(result, result.response?.status);
  }

  // Not `data!`: a 204 legitimately has no body, and those callers ignore the result.
  return result.data as T;
}

interface Envelope<T> {
  data?: T;
  error?: unknown;
  response?: Response;
}

// ── The surface the app uses ────────────────────────────────────────────────

export const api = {
  health: () => call(() => getHealthz()),

  search: (body: SearchApiRequest) => call(() => postApiSearch({ body })),

  listCorpora: () => call(() => getApiCorpora()),

  /**
   * The removals agents have asked for: the ones waiting, oldest first, or with `decided` the rest,
   * newest first. Each waiting one carries what it would take, worked out by the server when asked.
   */
  listProposals: (decided = false) => call(() => getApiProposals({ query: { decided } })),

  /** Runs the removal and records the approval together. Refused, with the reason, if it cannot be done now. */
  approveProposal: (id: string) => call(() => postApiProposalsByIdApprove({ path: { id } })),

  rejectProposal: (id: string) => call(() => postApiProposalsByIdReject({ path: { id } })),

  getCorpus: (nameOrId: string) => call(() => getApiCorporaByNameOrId({ path: { nameOrId } })),

  createCorpus: (body: CreateCorpusRequest) => call(() => postApiCorpora({ body })),

  updateCorpus: (nameOrId: string, body: UpdateCorpusRequest) =>
    call(() => patchApiCorporaByNameOrId({ path: { nameOrId }, body })),

  deleteCorpus: (nameOrId: string) =>
    call(() => deleteApiCorporaByNameOrId({ path: { nameOrId } })),

  addSource: (nameOrId: string, body: AddSourceRequest) =>
    call(() => postApiCorporaByNameOrIdSources({ path: { nameOrId }, body })),

  /** Change one source's filters. Omitted fields are left alone; names in `clear`
   *  return that field to the corpus default. */
  updateSource: (nameOrId: string, sourceId: string, body: UpdateSourceRequest) =>
    call(() => patchApiCorporaByNameOrIdSourcesBySourceId({ path: { nameOrId, sourceId }, body })),

  removeSource: (nameOrId: string, sourceId: string) =>
    call(() => deleteApiCorporaByNameOrIdSourcesBySourceId({ path: { nameOrId, sourceId } })),

  reindex: (nameOrId: string, full = false) =>
    call(() => postApiCorporaByNameOrIdReindex({ path: { nameOrId }, query: { full } })),

  /** Directories that lead to this corpus's sources but which no source covers. Reads
   *  the filesystem, so it is its own call rather than part of the corpus summary. */
  coverage: (nameOrId: string) =>
    call(() => getApiCorporaByNameOrIdCoverage({ path: { nameOrId } })),

  /**
   * One page of a corpus's files.
   *
   * `name` and `sort` go to the server rather than being applied to what came back: a
   * client can only filter what it fetched, so on a corpus larger than one page a name
   * that IS in the corpus came back as no match. The endpoint pages at 100 when asked
   * for no limit and clamps at 1,000.
   */
  listFiles: (
    nameOrId: string,
    opts: { status?: string; name?: string; sort?: string; limit?: number; offset?: number } = {},
  ) =>
    call(() => getApiCorporaByNameOrIdFiles({
      path: { nameOrId },
      query: {
        limit: opts.limit ?? 100,
        offset: opts.offset ?? 0,
        ...(opts.status ? { status: opts.status } : {}),
        ...(opts.name ? { name: opts.name } : {}),
        ...(opts.sort ? { sort: opts.sort } : {}),
      },
    })),

  /** One indexed file, stitched back together from its chunks. */
  fileText: (nameOrId: string, path: string, start?: number) =>
    call(() => getApiCorporaByNameOrIdFile({ path: { nameOrId }, query: { path, start } })),

  /**
   * @param activity drop the runs that found nothing to do, in the query. A page of
   * thirty is an hour of scheduled refreshes and days of real work.
   */
  listJobs: (limit = 30, activity?: boolean) =>
    call(() => getApiJobs({ query: { limit, activity } })),

  // ── Chunk sets ────────────────────────────────────────────────────────────

  listChunkSets: (corpus: string) =>
    call(() => getApiCorporaByNameOrIdChunkSets({ path: { nameOrId: corpus } })),

  createChunkSet: (corpus: string, body: CreateChunkSetRequest) =>
    call(() => postApiCorporaByNameOrIdChunkSets({ path: { nameOrId: corpus }, body })),

  updateChunkSet: (corpus: string, set: string, body: UpdateChunkSetRequest) =>
    call(() =>
      patchApiCorporaByNameOrIdChunkSetsBySetName({
        path: { nameOrId: corpus, setName: set },
        body,
      }),
    ),

  promoteChunkSet: (corpus: string, set: string) =>
    call(() =>
      postApiCorporaByNameOrIdChunkSetsBySetNamePromote({ path: { nameOrId: corpus, setName: set } }),
    ),

  deleteChunkSet: (corpus: string, set: string) =>
    call(() =>
      deleteApiCorporaByNameOrIdChunkSetsBySetName({ path: { nameOrId: corpus, setName: set } }),
    ),

  // ── Models ────────────────────────────────────────────────────────────────

  listEmbeddingProviders: () => call(() => getApiEmbeddingProviders()),

  listEmbeddingModels: (provider?: string) =>
    call(() => getApiEmbeddingModels({ query: provider ? { provider } : {} })),

  /** Takes a signal: the probe embeds two dozen inputs and can legitimately run for a
   *  minute, so the caller has to be able to stop waiting. */
  probeEmbeddingModel: (model: string, provider?: string, signal?: AbortSignal) =>
    call(() => postApiEmbeddingModelsProbe({
      body: { model, provider } satisfies ProbeModelRequest,
      signal,
    })),

  saveModelProfile: (body: SaveModelProfileRequest) =>
    call(() => putApiEmbeddingModelsProfile({ body })),

  deleteEmbeddingModel: (model: string, provider?: string) =>
    call(() =>
      deleteApiEmbeddingModelsByModel({ path: { model }, query: provider ? { provider } : {} }),
    ),

  // ── Documents ─────────────────────────────────────────────────────────────

  listDocuments: () => call(() => getApiDocuments()),

  documentText: (sha256: string) =>
    call(() => getApiDocumentsBySha256Text({ path: { sha256 } })),

  attachDocument: (corpus: string, sha256: string, fileName?: string) =>
    call(() =>
      postApiCorporaByNameOrIdDocumentsAttach({
        path: { nameOrId: corpus },
        body: { sha256, fileName } satisfies AttachDocumentRequest,
      }),
    ),

  detachDocument: (corpus: string, fileId: string) =>
    call(() =>
      deleteApiCorporaByNameOrIdDocumentsByFileId({ path: { nameOrId: corpus, fileId } }),
    ),

  /**
   * Upload is multipart and hand-rolled. The generated client models the body as a typed
   * object; a browser file upload is a FormData the browser must set its own boundary on,
   * so going through the generated path would mean fighting it to send what it already
   * knows how to send.
   *
   * Up to `UPLOAD_BATCH_FILES` files are one request, as they always were. More are sent in
   * sequential requests of that size, and the answers are merged into one `UploadResponse`:
   * `stored` and `failed` in request order, `corpus` from the first answered request and `job`
   * from the last.
   *
   * A request that fails is recorded in `failed` as a request-level entry (`file: null`)
   * carrying the error's message. A 4xx answer other than 401 and 403, such as the 400 for a
   * request in which every file was refused, does not stop the drop: the next request is still
   * sent, as the files after the refused ones were stored when the whole drop was one request.
   * Any other failure (401, 403, 5xx, no answer, an answer that is not an upload result) stops it,
   * because an expired token or a missing scope answers every request the same way, and each file
   * not yet sent is added to `failed` by name with the reason `Not sent`. If nothing was stored
   * and a request failed, the first failure is thrown, as for a single request.
   */
  uploadDocuments: async (corpus: string, files: File[]): Promise<UploadResponse> => {
    if (files.length <= UPLOAD_BATCH_FILES) return postUploadBatch(corpus, files);

    const stored: UploadResponse['stored'] = [];
    const failed: UploadResponse['failed'] = [];
    const errors: unknown[] = [];
    let first: UploadResponse | undefined;
    let last: UploadResponse | undefined;

    for (let start = 0; start < files.length; start += UPLOAD_BATCH_FILES) {
      const end = start + UPLOAD_BATCH_FILES;
      let answer: UploadResponse;
      try {
        answer = await postUploadBatch(corpus, files.slice(start, end));
      } catch (e) {
        errors.push(e);
        failed.push({ file: null, error: e instanceof Error ? e.message : String(e) });
        if (e instanceof ApiError && isRefusal(e.status)) continue;
        for (const f of files.slice(end)) failed.push({ file: f.name, error: UPLOAD_NOT_SENT });
        break;
      }
      first ??= answer;
      last = answer;
      stored.push(...answer.stored);
      failed.push(...answer.failed);
    }

    if (!first || !last || (stored.length === 0 && errors.length > 0)) throw errors[0];
    return { corpus: first.corpus, stored, failed, job: last.job };
  },

  // ── Access ────────────────────────────────────────────────────────────────

  listTokens: () => call(() => getApiTokens()),

  createToken: (name: string, scopes: string[], corpusIds?: string[], expiresInDays?: number) =>
    call(() => postApiTokens({
      body: { name, scopes, corpusIds, expiresInDays } satisfies CreateTokenRequest,
    })),

  revokeToken: (id: string) => call(() => deleteApiTokensById({ path: { id } })),

  browse: (path?: string) =>
    call(() => getApiWorkspaces({ query: path ? { path } : {} })),

  /**
   * What the repository in a workspace folder could be followed at. The empty string is
   * the workspace root, and is sent as such: an omitted path would mean the same, but the
   * root is a choice here, as it is in the picker.
   */
  /** `ref`, the ref a source follows, comes back resolved as git resolves it, in `listing.followed`. */
  repositoryRefs: (path: string, ref?: string) =>
    call(() => getApiWorkspacesGit({ query: { path, ref } })),

  /**
   * Exchange the admin password for a session bearer.
   *
   * The only anonymous call in the API. What comes back goes into the same
   * sessionStorage slot a pasted token used to occupy, so everything downstream is
   * unchanged and there is still no cookie.
   */
  signIn: (password: string) =>
    call(() => postApiSession({ body: { password } satisfies SignInRequest })),

  /**
   * End a session on the server: the one named, or the one stored when this is called.
   *
   * Sent with the request rather than left to the interceptor, which reads storage when
   * the request goes out. By then the caller has cleared it: the DELETE went out with no
   * token, the server had nothing to revoke and answered 204 anyway, and the session
   * stayed usable until it expired.
   */
  signOut: (token: string | null = getToken()) => {
    if (!token) return Promise.resolve();
    return call(() => deleteApiSession({ headers: { Authorization: `Bearer ${token}` } }));
  },

  /**
   * Replace which corpora a key may reach. An empty list means every corpus.
   *
   * Read per request on the server, so this reaches a running agent on its next call
   * without it reconnecting. That is the whole reason the mapping lives here rather than
   * in a header in the agent's own configuration.
   */
  setTokenCorpora: (id: string, corpusIds: string[]) =>
    call(() => putApiTokensByIdCorpora({
      path: { id },
      body: { corpusIds } satisfies UpdateTokenCorporaRequest,
    })),

  /**
   * Replace a key's scopes. The server drops its cached principals, so a scope removed is
   * refused from the agent's next call; a tool a scope adds is listed when its client next
   * reconnects, since the MCP transport cannot tell it the list changed.
   */
  setTokenScopes: (id: string, scopes: string[]) =>
    call(() => putApiTokensByIdScopes({
      path: { id },
      body: { scopes } satisfies UpdateTokenScopesRequest,
    })),
};

function authHeaders(): HeadersInit {
  const token = getToken();
  return token ? { Authorization: `Bearer ${token}` } : {};
}

/**
 * Files per upload request. Mirrors the server's `UploadOptions.BatchFiles`
 * (src/Dexicon.Core/Configuration/DexiconOptions.cs): it reads that many file parts from one
 * request and refuses the rest. The limit is a compile-time constant there and appears in
 * neither the OpenAPI document nor a settings endpoint, so it is repeated here.
 */
export const UPLOAD_BATCH_FILES = 10;

/** The reason given for a file that a stopped upload never sent. */
const UPLOAD_NOT_SENT = 'Not sent: an earlier request failed.';

/** Whether a failed request's status says only that its own files were refused, so the next request may succeed. */
function isRefusal(status: number): boolean {
  return status >= 400 && status < 500 && status !== 401 && status !== 403;
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null;
}

/** The parsed JSON, or `undefined` when the text is empty or is not JSON (an HTML error page, say). */
function parseJson(text: string): unknown {
  try {
    return text ? JSON.parse(text) : undefined;
  } catch {
    return undefined;
  }
}

function isUploadResponse(v: unknown): v is UploadResponse {
  return isRecord(v) && Array.isArray(v.stored) && Array.isArray(v.failed);
}

/**
 * One multipart POST of `files` to a corpus. Rejects with an `ApiError` on a non-2xx answer,
 * built from the problem details when the body is JSON and from the status text when it is not,
 * and on a 2xx answer whose body is not an upload result.
 */
async function postUploadBatch(corpus: string, files: File[]): Promise<UploadResponse> {
  const form = new FormData();
  for (const f of files) form.append('files', f, f.name);

  const res = await fetch(`/api/corpora/${encodeURIComponent(corpus)}/documents`, {
    method: 'POST',
    headers: authHeaders(),
    body: form,
  });

  const body = parseJson(await res.text());
  if (!res.ok) {
    const problem = isRecord(body) ? body : {};
    throw new ApiError(
      res.status,
      typeof problem.title === 'string' ? problem.title : res.statusText || 'Request failed',
      typeof problem.detail === 'string' ? problem.detail : undefined,
    );
  }
  if (!isUploadResponse(body)) {
    throw new ApiError(
      res.status,
      'Unexpected response',
      'The server answered the upload with something that is not an upload result.',
    );
  }
  return body;
}

/**
 * What `/api/events` actually puts on the wire: `IndexProgress`, serialised as it stands.
 *
 * NOT a `JobSummary`, which is what this was typed as. The two differ in the two fields
 * anything reading the stream reaches for first — it carries `jobId` rather than `id`,
 * and it has no `state` at all — so the compiler cheerfully agreed with code that read
 * `p.state` and compared `p.id`, and every one of those comparisons was quietly false
 * forever. Captured from a live run rather than read off the record:
 *
 *   keys:  jobId, corpusId, phase, filesTotal, filesDone, filesSkipped, filesFailed,
 *          chunksWritten, currentFile, error
 *   phase: "discover", "extract", … while working, then the STATE NAME — "Succeeded" —
 *          because the server nulls Phase before the last report and sends
 *          `job.Phase ?? job.State.ToString()`.
 *
 * So `phase` is the whole signal, and "finished" is a phase that names a terminal state
 * rather than a phase that is absent.
 */
export type Progress = {
  jobId: string;
  corpusId: string;
  phase: string;
  filesTotal: number;
  filesDone: number;
  filesSkipped: number;
  filesFailed: number;
  chunksWritten: number;
  currentFile?: string | null;
  error?: string | null;
};

/** The phases that mean the run is over. They are `JobState` names, capitalised by .NET. */
const FINISHED_PHASES = new Set(['succeeded', 'failed', 'degraded', 'cancelled']);

/** Whether a progress event describes a run that is still going. */
export const isRunning = (p: Progress | undefined): boolean =>
  p !== undefined && !FINISHED_PHASES.has(String(p.phase ?? '').toLowerCase());

/** A running job from the jobs listing, in the shape the stream would have sent. */
export const progressOf = (j: JobSummary): Progress => ({
  jobId: j.id,
  corpusId: j.corpusId,
  phase: j.phase ?? j.state,
  filesTotal: j.filesTotal,
  filesDone: j.filesDone,
  filesSkipped: j.filesSkipped,
  filesFailed: j.filesFailed,
  chunksWritten: j.chunksWritten,
  error: j.error,
});

/**
 * Live indexing progress, for as long as the page is open.
 *
 * `fetch` rather than `EventSource`, which cannot carry an Authorization header, and the
 * token is intentionally not a cookie.
 *
 * RECONNECTS. The first version read the stream once: a normal end broke the loop without
 * telling anyone, and an error reported itself and then stopped. Either way progress went
 * quiet for the life of the page while the header still said "connected", and the only
 * cure was a reload. Every server restart did it, which is why it looked intermittent.
 *
 * `onOpen` exists because the header offers to say "reconnecting" and nothing could ever
 * take that back.
 */
export function subscribeToProgress(
  onProgress: (p: Progress) => void,
  onError?: () => void,
  onOpen?: () => void,
): () => void {
  const controller = new AbortController();
  const { signal } = controller;

  void (async () => {
    let attempt = 0;

    while (!signal.aborted) {
      try {
        const res = await fetch('/api/events', { headers: authHeaders(), signal });
        if (!res.ok || !res.body) throw new Error(`events: ${res.status}`);

        attempt = 0;
        onOpen?.();

        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        for (;;) {
          const { done, value } = await reader.read();
          if (done) break;

          buffer += decoder.decode(value, { stream: true });
          const frames = buffer.split('\n\n');
          buffer = frames.pop() ?? '';

          for (const frame of frames) {
            const line = frame.split('\n').find((l) => l.startsWith('data: '));
            if (line) onProgress(JSON.parse(line.slice(6)));
          }
        }

        // The stream ended on its own. Not an error, and not a reason to stop: a server
        // that restarted mid-index has progress to report the moment it is back.
        if (!signal.aborted) onError?.();
      } catch (e) {
        if ((e as Error).name === 'AbortError' || signal.aborted) return;
        onError?.();
      }

      if (signal.aborted) return;

      // Backoff with jitter, capped. A page left open against a server that is down must
      // not spend the night retrying every 500ms.
      const delay = Math.min(30_000, 500 * 2 ** attempt) + Math.random() * 250;
      attempt += 1;
      await wait(delay, signal);
    }
  })();

  return () => controller.abort();
}

/** A delay that gives up when the caller does, so unsubscribing is immediate. */
function wait(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const finish = () => {
      clearTimeout(timer);
      signal.removeEventListener('abort', finish);
      resolve();
    };
    const timer = setTimeout(finish, ms);
    signal.addEventListener('abort', finish, { once: true });
  });
}
