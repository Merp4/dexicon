import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  api,
  ApiError,
  getToken,
  setToken,
  subscribeToProgress,
  type Corpus,
  type Health,
  type IndexedFile,
  type EmbeddingModelInfo,
  type IndexedFileText,
  type Job,
  type SearchResult,
} from './api';
import {
  Badge, Button, CardButton, Checkbox, Chip, CopyButton, Empty, ErrorBanner, Field, Input, Modal,
  Segmented, Select, SelectItem, Spinner, formatBytes, localTime, relativeTime, stateTone,
} from './ui';
import {
  Check, Database, FileText, Key, ListChecks, LogOut, Plus, RefreshCw, RotateCcw, Search,
  Settings, Sliders, Trash2,
} from 'lucide-react';
import { cn } from 'cn';
import { WorkspacePicker } from './WorkspacePicker';

/** `nomic-embed-text` and `nomic-embed-text:latest` are the same model. */
const sameModelName = (a: string, b: string) =>
  a.replace(/:latest$/i, '').toLowerCase() === b.replace(/:latest$/i, '').toLowerCase();

import { DocumentsView } from './Documents';
import { ChunkSetsPanel, ModelsView } from './ChunkSets';

type View = 'search' | 'corpora' | 'documents' | 'jobs' | 'models' | 'access' | 'settings';

/**
 * Sentinels for "no particular one".
 *
 * A select option has to carry a value, and the empty string is not one: that is how the
 * control says nothing is chosen. The leading colon makes this impossible to collide
 * with a real corpus name, because a colon is what separates corpus from chunk set. It
 * never leaves the component that uses it; the state it maps to is still `[]`.
 */
const ALL_CORPORA = ':all';

/**
 * The UI exists to answer four questions and to do nothing else:
 *   1. What is indexed, and is it current?
 *   2. What is the indexer doing now, and did anything fail?
 *   3. Does search return what I expect?
 *   4. Who can see what?
 */
export default function App() {
  const [token, setTok] = useState<string | null>(getToken());
  if (!token) return <TokenGate onToken={(t) => { setToken(t); setTok(t); }} />;
  return <Shell onSignOut={() => { setToken(null); setTok(null); }} />;
}

function TokenGate({ onToken }: { onToken: (t: string) => void }) {
  const [value, setValue] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setToken(value.trim());
    try {
      await api.health();
      onToken(value.trim());
    } catch (err) {
      setToken(null);
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid min-h-[100dvh] place-items-center p-4">
      <form onSubmit={submit} className="card p-6 w-[100%] max-w-[480px]">
        <h1 className="mt-0 mx-0 mb-1 text-xl">Dexicon</h1>
        <p className="dim mt-0 mx-0 mb-5 text-sm">
          Paste an API token to continue.
        </p>

        <Field
          label="API token"
          hint="On a fresh install the bootstrap token is printed once in the container log: docker compose logs dexicon | grep bootstrap"
        >
          <Input
            className="font-mono"
            type="password"
            autoComplete="off"
            placeholder="dex_…"
            value={value}
            onChange={(e) => setValue(e.target.value)}
          />
        </Field>

        {error != null && <div className="mb-3.5"><ErrorBanner error={error} /></div>}

        <Button variant="primary" type="submit" disabled={!value.trim() || busy} className="w-[100%] justify-center">
          {busy ? <Spinner /> : null} Continue
        </Button>
      </form>
    </div>
  );
}

function Shell({ onSignOut }: { onSignOut: () => void }) {
  const [view, setView] = useState<View>('search');
  const [health, setHealth] = useState<Health | null>(null);
  const [corpora, setCorpora] = useState<Corpus[]>([]);
  const [live, setLive] = useState<Record<string, Job & { currentFile?: string }>>({});
  const [connected, setConnected] = useState(true);
  const [healthStale, setHealthStale] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [selected, setSelected] = useState<string | null>(null);

  const refreshCorpora = useCallback(async () => {
    try {
      setCorpora(await api.listCorpora());
    } catch (e) {
      setError(e);
    }
  }, []);

  useEffect(() => {
    void refreshCorpora();
    const tick = async () => {
      try {
        setHealth(await api.health());
        setHealthStale(false);
      } catch (e) {
        if (e instanceof ApiError && e.status === 401) { onSignOut(); return; }
        // A failed poll is NOT an outage. /healthz can be slow while indexing
        // saturates Ollama, and blanking the state would paint both dots red during
        // perfectly normal work, and a false alarm is as bad as a missed one. Keep the
        // last known state and say that it is stale.
        setHealthStale(true);
      }
    };
    void tick();
    const id = setInterval(tick, 15000);
    return () => clearInterval(id);
  }, [refreshCorpora, onSignOut]);

  useEffect(
    () =>
      subscribeToProgress(
        (p) => {
          setLive((prev) => ({ ...prev, [p.corpusId]: p }));
          // A job reaching a terminal state changes the corpus counts too.
          if (p.state && !['running', 'queued'].includes(String(p.phase ?? ''))) void refreshCorpora();
        },
        () => setConnected(false),
      ),
    [refreshCorpora],
  );

  const nav = [
    { id: 'search', label: 'Search', Icon: Search },
    { id: 'corpora', label: 'Corpora', Icon: Database },
    { id: 'documents', label: 'Documents', Icon: FileText },
    { id: 'jobs', label: 'Jobs', Icon: ListChecks },
    { id: 'models', label: 'Models', Icon: Sliders },
    { id: 'access', label: 'Access', Icon: Key },
    { id: 'settings', label: 'Settings', Icon: Settings },
  ] satisfies { id: View; label: string; Icon: typeof Search }[];

  return (
    <div className="flex flex-col min-h-[100dvh]">
      <header
        className="flex flex-wrap items-center gap-4 border-b border-border bg-card px-4 py-2.5"
      >
        <strong className="text-base">Dexicon</strong>

        <nav className="flex gap-1 flex-1 flex-wrap">
          {nav.map((n) => (
            <Chip
              key={n.id}
              active={view === n.id}
              flat
              // `false`, not undefined, on the others: it is a valid aria-current value that
              // screen readers treat as absent, and it keeps every item in the bar
              // declaring the same kind of thing. Leaving it off made Chip fall back to
              // toggle semantics and announce the six you are NOT on as buttons you had
              // not pressed.
              aria-current={view === n.id ? 'page' : false}
              onClick={() => { setView(n.id); setSelected(null); }}
            >
              <n.Icon />
              {n.label}
            </Chip>
          ))}
        </nav>

        <HealthDots health={health} connected={connected} stale={healthStale} />
        <Button onClick={onSignOut}>
          <LogOut />
          Sign out
        </Button>
      </header>

      <main className="mx-auto w-full max-w-[1180px] flex-1 p-4">
        {error != null && (
          <div className="mb-4">
            <ErrorBanner error={error} onDismiss={() => setError(null)} />
          </div>
        )}

        {view === 'search' && <SearchView corpora={corpora} onError={setError} />}
        {view === 'corpora' && !selected && (
          <CorporaView corpora={corpora} live={live} onRefresh={refreshCorpora} onOpen={setSelected} onError={setError} />
        )}
        {view === 'corpora' && selected && (
          <CorpusDetail name={selected} live={live} onBack={() => setSelected(null)} onRefresh={refreshCorpora} onError={setError} />
        )}
        {view === 'documents' && (
          <DocumentsView corpora={corpora} onError={setError} onRefresh={refreshCorpora} />
        )}
        {view === 'jobs' && <JobsView corpora={corpora} live={live} onError={setError} />}
        {view === 'access' && <AccessView onError={setError} />}
        {view === 'models' && <ModelsView />}
        {view === 'settings' && <SettingsView health={health} />}
      </main>
    </div>
  );
}

function HealthDots({ health, connected, stale }: { health: Health | null; connected: boolean; stale: boolean }) {
  const [open, setOpen] = useState(false);
  // A dev tool that hides whether its dependencies are healthy wastes an hour of
  // someone's day per incident. One that cries wolf wastes just as much, so an
  // unknown state is grey, never red.
  const dot = (ok: boolean | undefined) => ({
    width: 8,
    height: 8,
    borderRadius: '50%',
    background: ok === undefined ? 'var(--text-dim)' : ok ? 'var(--ok)' : 'var(--danger)',
    opacity: stale ? 0.55 : 1,
    display: 'inline-block',
  });

  return (
    <div className="relative">
      <Button onClick={() => setOpen((v) => !v)} aria-expanded={open} title="Dependency health">
        <span style={dot(health?.qdrant.reachable)} /> qdrant
        <span className="ml-1.5" style={dot(health?.ollama.reachable)} /> ollama
        {stale && <span className="dim ml-1.5 text-xs">· checking</span>}
        {!connected && <span className="dim ml-1.5 text-xs">· reconnecting</span>}
      </Button>

      {open && health && (
        <div className="card absolute top-10 right-0 z-20 w-[330px] p-3 text-xs">
          <p className="mt-0 mx-0 mb-2 font-semibold">Qdrant</p>
          <p className="dim mono m-0 break-all">{health.qdrant.endpoint}</p>
          <p className="mt-1 mx-0 mb-3">
            <Badge tone={health.qdrant.reachable ? 'ok' : 'danger'}>{health.qdrant.reachable ? 'reachable' : 'unreachable'}</Badge>
          </p>

          <p className="mt-0 mx-0 mb-2 font-semibold">Ollama</p>
          <p className="dim mono m-0 break-all">{health.ollama.endpoint}</p>
          <p className="mt-1 mx-0 mb-0">
            <Badge tone={health.ollama.reachable ? 'ok' : 'danger'}>{health.ollama.model}</Badge>{' '}
            <Badge>{health.ollama.dimensions}d</Badge>
          </p>
          {health.ollama.error && (
            <p className="mt-2 mx-0 mb-0 text-[var(--danger)]">{health.ollama.error}</p>
          )}
          {!health.ollama.reachable && (
            <p className="dim mt-2 mx-0 mb-0">
              Search still works in keyword mode. Indexing will back off until it recovers.
            </p>
          )}
        </div>
      )}
    </div>
  );
}

// ── Search ──────────────────────────────────────────────────────────────────

export function SearchView({ corpora, onError }: { corpora: Corpus[]; onError: (e: unknown) => void }) {
  const [query, setQuery] = useState('');
  const [mode, setMode] = useState<'hybrid' | 'semantic' | 'keyword'>('hybrid');
  const [scope, setScope] = useState<string[]>([]);
  const [limit, setLimit] = useState(10);
  const [result, setResult] = useState<SearchResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [explain, setExplain] = useState(false);
  const [viewing, setViewing] = useState<{ corpus: string; path: string; line?: number } | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === '/' && document.activeElement?.tagName !== 'INPUT') {
        e.preventDefault();
        inputRef.current?.focus();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);

  async function run(e?: React.FormEvent) {
    e?.preventDefault();
    if (!query.trim()) return;
    setBusy(true);
    try {
      setResult(await api.search({ query, mode, limit, corpus: scope.length ? scope : undefined }));
    } catch (err) {
      onError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid gap-4">
      <form onSubmit={run} className="card p-4 grid gap-3">
        <Input
          ref={inputRef}
          className="h-11 text-base"
          placeholder="Ask a question, or paste a code fragment…   (press / to focus)"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          // Explicit rather than trusting the form's implicit submission. The box asks a
          // question, so Enter has to answer it; preventDefault keeps this the only
          // submit even where the browser would also have done it.
          onKeyDown={(e) => {
            if (e.key !== 'Enter') return;
            e.preventDefault();
            void run();
          }}
        />

        <div className="flex gap-3 flex-wrap items-center">
          <Segmented
            label="Search mode"
            value={mode}
            onChange={setMode}
            options={[
              { value: 'hybrid', label: 'hybrid', title: 'Meaning and exact terms together' },
              { value: 'semantic', label: 'semantic', title: 'Meaning only' },
              { value: 'keyword', label: 'keyword', title: 'Exact matches only; works without embeddings' },
            ]}
          />

          <Select
            className="w-auto min-w-[170px]"
            value={scope.length === 1 ? scope[0] : ALL_CORPORA}
            onValueChange={(v) => setScope(v === ALL_CORPORA ? [] : [v])}
            aria-label="Corpus scope"
          >
            <SelectItem value={ALL_CORPORA}>All visible corpora</SelectItem>
            {corpora.map((c) => (
              <SelectItem key={c.id} value={c.name}>{c.name}</SelectItem>
            ))}
          </Select>

          <Select
            className="w-auto"
            value={String(limit)}
            onValueChange={(v) => setLimit(Number(v))}
            aria-label="Result limit"
          >
            {[5, 10, 20, 50].map((n) => (
              <SelectItem key={n} value={String(n)}>{n} results</SelectItem>
            ))}
          </Select>

          {/* size="default" is h-9, matching the Input, the Segmented control and both
              Selects on this row. Button defaults to the app's compact h-8, which left
              the one control that commits the search sitting 4px shorter than everything
              beside it. */}
          <Button variant="primary" size="default" type="submit" disabled={busy || !query.trim()}>
            {busy ? <Spinner /> : <Search />}
            Search
          </Button>
        </div>
      </form>

      {result?.degraded && (
        <div className="card border-[color-mix(in_oklab,var(--warn)_45%,transparent)] bg-[color-mix(in_oklab,var(--warn)_8%,transparent)] px-3.5 py-3 text-sm">
          <strong>Degraded:</strong> {result.degradedReason}
        </div>
      )}
      {result?.note && (
        <div className="card border-[color-mix(in_oklab,var(--accent)_40%,transparent)] px-3.5 py-3 text-sm">
          {result.note}
        </div>
      )}

      {result && (
        <>
          <div className="flex items-center gap-2.5 text-sm flex-wrap">
            <span className="dim">
              {result.hits.length} result{result.hits.length === 1 ? '' : 's'} · {result.tookMs} ms
            </span>
            <Button onClick={() => setExplain((v) => !v)} className="py-0.5 px-2 text-xs">
              {explain ? 'Hide' : 'Explain'}
            </Button>
          </div>

          {/* How to tell "the index is bad" from "the scope was wrong": the single
              most useful thing this screen can show. */}
          {explain && (
            <div className="card p-3 text-xs grid gap-1">
              <div><span className="dim">resolved scope:</span> {result.scope.map((s) => s.name).join(', ') || '(none)'}</div>
              <div><span className="dim">mode used:</span> {result.mode}{result.degraded ? ' (degraded from requested)' : ''}</div>
              <div><span className="dim">scores:</span> reciprocal rank fusion, k=2. Ordering is meaningful, magnitude is not</div>
            </div>
          )}

          {result.hits.length === 0 ? (
            <Empty
              title="Nothing matched"
              hint="If that is unexpected, check Jobs: the corpus may still be indexing, or the content may not be indexed at all."
            />
          ) : (
            <div className="grid gap-3">
              {result.hits.map((h, i) => (
                <article key={`${h.corpusId}-${h.filePath}-${h.startLine}-${i}`} className="card p-3.5">
                  {/* The citation truncates; the actions do not move.
                      A book's filename is long — "Coaching Agile Teams - A Companion for
                      ScrumMasters, Agile Coaches, and Project Managers in Transition.epub"
                      — and wrapping it pushed Copy path and Open onto a second line, so
                      the controls sat in a different place on every result. The full text
                      is still on the element and in Copy path, which is how anyone
                      actually takes a citation. */}
                  <header className="flex gap-2.5 items-center mb-2">
                    <code className="mono truncate text-sm font-semibold" title={h.location ?? undefined}>
                      {h.location}
                    </code>
                    {h.section && <span className="dim shrink-0 text-xs">· {h.section}</span>}
                    <span className="flex-1" />
                    {h.language && <Badge>{h.language}</Badge>}
                    {result.scope.length > 1 && h.corpusName && <Badge tone="accent">{h.corpusName}</Badge>}
                    <CopyButton text={h.location ?? ''} label="Copy path" />
                    {/* A snippet is forty lines out of a file. Reading on from it used to
                        mean leaving for an editor, which for an uploaded PDF is nowhere. */}
                    {h.corpusName && (
                      <Button
                        className="px-2 py-0.5 text-xs"
                        onClick={() => setViewing({
                          corpus: h.corpusName!,
                          path: h.filePath,
                          line: h.startLine,
                        })}
                      >
                        <FileText />
                        Open
                      </Button>
                    )}
                  </header>
                  <pre className="mono m-0 max-h-[340px] overflow-x-auto rounded-md bg-muted px-3 py-2.5 text-xs break-words whitespace-pre-wrap">
                    {h.content}
                  </pre>
                </article>
              ))}
            </div>
          )}
        </>
      )}

      {viewing && (
        <FileViewer
          corpus={viewing.corpus}
          path={viewing.path}
          aroundLine={viewing.line}
          onClose={() => setViewing(null)}
          onError={onError}
        />
      )}

      {!result && (
        <Empty
          title="Search your indexed content"
          hint={corpora.length === 0 ? 'No corpora yet. Create one under Corpora first.' : 'Hybrid blends meaning with exact terms. Keyword works even when embeddings are down.'}
        />
      )}
    </div>
  );
}

// ── Corpora ─────────────────────────────────────────────────────────────────

export function CorporaView({
  corpora,
  live,
  onRefresh,
  onOpen,
  onError,
}: {
  corpora: Corpus[];
  live: Record<string, Job & { currentFile?: string }>;
  onRefresh: () => Promise<void>;
  onOpen: (name: string) => void;
  onError: (e: unknown) => void;
}) {
  const [creating, setCreating] = useState(false);

  return (
    <div className="grid gap-4">
      <div className="flex justify-between items-center">
        <h1 className="m-0 text-lg">Corpora</h1>
        <Button variant="primary" onClick={() => setCreating(true)}><Plus />New corpus</Button>
      </div>

      {corpora.length === 0 ? (
        <Empty
          title="No corpora yet"
          hint="A corpus is a named body of content: a repository, a folder of docs, a PDF library."
          action={<Button variant="primary" onClick={() => setCreating(true)}><Plus />Create the first one</Button>}
        />
      ) : (
        <div className="grid gap-3">
          {corpora.map((c) => {
            const job = live[c.id];
            const running = job && (job.state === 'running' || job.phase);
            return (
              <CardButton key={c.id} onClick={() => onOpen(c.name)}>
                <div className="flex gap-2 items-center flex-wrap">
                  <strong>{c.name}</strong>
                  <Badge tone={stateTone(c.state)}>{c.state}</Badge>
                  {c.visibility === 'shared' && <Badge tone="accent">shared</Badge>}
                  {!c.owned && <Badge>read-only</Badge>}
                  <span className="flex-1" />
                  <span className="dim text-xs" title={localTime(c.lastIndexedUtc)}>indexed {relativeTime(c.lastIndexedUtc)}</span>
                </div>

                {c.description && <p className="dim mt-1.5 mx-0 mb-0 text-sm">{c.description}</p>}

                <div className="dim mt-2 text-xs flex gap-3.5 flex-wrap">
                  <span>{c.fileCount.toLocaleString()} files</span>
                  <span>{c.chunkCount.toLocaleString()} chunks</span>
                  {c.skippedCount > 0 && <span>{c.skippedCount.toLocaleString()} skipped</span>}
                  {c.failedCount > 0 && <span className="text-[var(--danger)]">{c.failedCount.toLocaleString()} failed</span>}
                  {/* The default set is what this corpus answers to unqualified. */}
                  <span className="mono">
                    {c.chunkSets.find((s) => s.isDefault)?.embeddingModel ?? c.chunkSets[0]?.embeddingModel ?? '—'}
                  </span>
                  {c.chunkSets.length > 1 && <span>{c.chunkSets.length} chunk sets</span>}
                </div>

                {running && <ProgressBar job={job} />}
              </CardButton>
            );
          })}
        </div>
      )}

      {creating && <CreateCorpusModal onClose={() => setCreating(false)} onCreated={async () => { setCreating(false); await onRefresh(); }} onError={onError} />}
    </div>
  );
}

function ProgressBar({ job }: { job: Job & { currentFile?: string } }) {
  const processed = job.filesDone + job.filesSkipped + job.filesFailed;
  const pct = job.filesTotal > 0 ? Math.min(100, (processed / job.filesTotal) * 100) : 0;
  return (
    <div className="mt-2.5">
      <div className="h-[5px] overflow-hidden rounded-full bg-muted">
        <div
          className="h-full bg-[var(--accent)] transition-[width] duration-300"
          // Live percentage: the one thing that cannot be a class without generating a
          // class per percent.
          style={{ width: `${pct}%` }}
        />
      </div>
      <div className="dim text-xs mt-1">
        {job.phase ?? job.state} · {processed.toLocaleString()}/{job.filesTotal.toLocaleString()} files · {job.chunksWritten.toLocaleString()} chunks
        {job.currentFile ? ` · ${job.currentFile}` : ''}
      </div>
    </div>
  );
}

function CreateCorpusModal({ onClose, onCreated, onError }: { onClose: () => void; onCreated: () => Promise<void>; onError: (e: unknown) => void }) {
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [path, setPath] = useState('');
  const [models, setModels] = useState<EmbeddingModelInfo[]>([]);
  const [model, setModel] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    // The model belongs here, not only on the chunk set form. A corpus is born with a
    // set, and that set's model is the one thing about it that cannot be edited later,
    // because a different model is a different vector space. Leaving it out meant every
    // corpus made in this UI took the configured default, and changing it afterwards
    // meant building a second set and promoting it.
    api.listEmbeddingModels()
      .then((r) => {
        setModels(r.models);
        // The server's configured default, matched to what the provider actually lists
        // (`nomic-embed-text` there, `nomic-embed-text:latest` here).
        const configured = r.models.find((m) => sameModelName(m.name, r.configured));
        setModel(configured?.name ?? r.models[0]?.name ?? '');
      })
      .catch(() => setModels([]));
  }, []);

  const chosen = models.find((m) => m.name === model);
  const measured = chosen?.measured ?? null;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    try {
      await api.createCorpus({
        name,
        description: description || undefined,
        workspacePath: path || undefined,
        embeddingModel: model || undefined,
        // Only when the model has been measured. Sending a guess would pin a size into
        // the one property of a chunk set nobody can edit afterwards.
        chunkSize: measured?.recommendedChunkTokens,
        chunkOverlap: measured ? Math.max(1, Math.round(measured.recommendedChunkTokens / 8)) : undefined,
      });
      await onCreated();
    } catch (err) {
      onError(err);
      setBusy(false);
    }
  }

  return (
    <Modal title="New corpus" onClose={onClose}>
      <form onSubmit={submit}>
        <Field label="Name" hint="Agents pass this to search_index, so keep it short and memorable.">
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="api-repo" autoFocus />
        </Field>
        <Field
          label="Description"
          hint="An agent reads this to choose which corpus answers a question. Say what is in it and what it is good for."
        >
          <Input
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Architecture and .NET books: design trade-offs, not API reference"
          />
        </Field>
        <Field
          label="Embedding model"
          hint={
            measured
              ? `Measured at ${measured.maxInputChars?.toLocaleString() ?? 'no'} chars` +
                `${measured.charsPerToken ? ` · ${measured.charsPerToken} chars/token` : ''}` +
                ` · chunks of ${measured.recommendedChunkTokens.toLocaleString()} tokens`
              : 'Fixed once the corpus exists: a different model is a different vector space, so changing it later means a new chunk set. Test limits on Models measures one.'
          }
        >
          {models.length > 0 ? (
            <Select value={model} onValueChange={setModel}>
              {models.map((m) => (
                <SelectItem key={m.name} value={m.name}>
                  {m.name} ({formatBytes(m.sizeBytes)})
                  {m.measured ? ` · ${m.measured.recommendedChunkTokens.toLocaleString()} tokens` : ''}
                </SelectItem>
              ))}
            </Select>
          ) : (
            <Input value={model} onChange={(e) => setModel(e.target.value)} placeholder="embeddinggemma" />
          )}
        </Field>

        <Field
          label="Workspace folder"
          hint="Only paths bind-mounted into the container are reachable. Set WORKSPACE_ROOT to change what is available."
        >
          <WorkspacePicker value={path} onChange={setPath} emptyLabel="(add a source later)" disabled={busy} />
        </Field>
        <div className="flex gap-2 justify-end mt-4">
          <Button type="button" onClick={onClose}>Cancel</Button>
          <Button variant="primary" type="submit" disabled={!name.trim() || busy}>
            {busy ? <Spinner /> : null} Create
          </Button>
        </div>
      </form>
    </Modal>
  );
}

export function CorpusDetail({
  name,
  live,
  onBack,
  onRefresh,
  onError,
}: {
  name: string;
  live: Record<string, Job & { currentFile?: string }>;
  onBack: () => void;
  onRefresh: () => Promise<void>;
  onError: (e: unknown) => void;
}) {
  const [corpus, setCorpus] = useState<Corpus | null>(null);
  const [files, setFiles] = useState<IndexedFile[]>([]);
  const [filter, setFilter] = useState<string>('');
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [removingSource, setRemovingSource] = useState<Corpus['sources'][number] | null>(null);
  const [addingSource, setAddingSource] = useState(false);
  const [viewing, setViewing] = useState<{ path: string; line?: number } | null>(null);


  const load = useCallback(async () => {
    try {
      const c = await api.getCorpus(name);
      setCorpus(c);
      const f = await api.listFiles(name, filter || undefined);
      setFiles(f.files);
    } catch (e) {
      onError(e);
    }
  }, [name, filter, onError]);

  useEffect(() => { void load(); }, [load]);

  const job = corpus ? live[corpus.id] : undefined;
  useEffect(() => {
    if (job && !job.phase) void load();
  }, [job, load]);

  if (!corpus) return <Empty title="Loading…" />;

  const problems = files.filter((f) => f.status !== 'indexed');

  return (
    <div className="grid gap-4">
      <div className="flex gap-2.5 items-center flex-wrap">
        <Button onClick={onBack}>← Corpora</Button>
        <h1 className="m-0 text-lg">{corpus.name}</h1>
        <Badge tone={stateTone(corpus.state)}>{corpus.state}</Badge>
        <span className="flex-1" />
        {corpus.owned && (
          <>
            <Button onClick={async () => { try { await api.reindex(corpus.name); } catch (e) { onError(e); } }}><RefreshCw />Refresh</Button>
            <Button onClick={async () => { try { await api.reindex(corpus.name, true); } catch (e) { onError(e); } }}><RotateCcw />Full reindex</Button>
            <Button variant="danger" onClick={() => setConfirmDelete(true)}><Trash2 />Delete</Button>
          </>
        )}
      </div>

      {job?.phase && <div className="card p-3.5"><ProgressBar job={job} /></div>}

      <div className="card p-3.5 grid gap-2 text-sm">
        <Row label="Sources">
          {corpus.sources.length === 0 ? (
            <span className="dim">none: add one, or upload documents</span>
          ) : (
            <span className="grid gap-1">
              {corpus.sources.map((s) => (
                <span key={s.id} className="flex flex-wrap items-baseline gap-2">
                  <span className="mono">{s.rootPath ?? s.kind}</span>
                  {/* What this source is actually doing. The filters were settable and
                      invisible, which is the worst of both.

                      `?.` is not paranoia about a field the contract marks required. The
                      dev loop is a supported workflow — Vite serving this UI against a
                      container built earlier — so the UI can genuinely be ahead of the
                      API, and it was: the first run of this screen against an older
                      server blanked the page on `undefined.length`. In the image they
                      always ship together; here they do not. */}
                  {/* What it brought in. A corpus with one source does not need this:
                      the corpus total IS the source total — but a corpus with ten does:
                      a folder contributing nothing is what a mistyped path, an over-eager
                      exclude glob and an index that stopped early all look like, and it
                      is invisible in a corpus-level count. */}
                  {corpus.sources.length > 1 && (
                    <span className={s.fileCount ? 'dim text-xs' : 'text-xs text-[var(--warn)]'}>
                      {s.fileCount ? `${s.fileCount.toLocaleString()} files` : 'no files'}
                    </span>
                  )}
                  <span className="dim text-xs">
                    {s.useGitignore ? '.gitignore honoured' : '.gitignore ignored'}
                    {' · '}≤ {formatBytes(s.maxFileBytes)}
                    {s.includeGlobs?.length ? ` · only ${s.includeGlobs.join(', ')}` : ''}
                    {s.excludeGlobs?.length ? ` · not ${s.excludeGlobs.join(', ')}` : ''}
                  </span>
                  {/* Adding a folder was one click; removing one meant deleting the whole
                      corpus and rebuilding it, losing its chunk sets, its history and every
                      other source with it. A path typed wrong is not worth that. */}
                  {corpus.owned && (
                    <Button
                      variant="ghost"
                      size="icon-xs"
                      aria-label={`Remove source ${s.rootPath ?? s.kind}`}
                      onClick={() => setRemovingSource(s)}
                    >
                      <Trash2 />
                    </Button>
                  )}
                </span>
              ))}
            </span>
          )}
          {corpus.owned && (
            <Button className="mt-1.5 px-2 py-0.5 text-xs" onClick={() => setAddingSource(true)}>
              <Plus />
              Add source
            </Button>
          )}
        </Row>
        <Row label="Searched as">
          <span className="mono">{corpus.name}</span>
          <span className="dim">
            {' '}is the default set below. Name another with <span className="mono">corpus:set</span>.
          </span>
        </Row>
        <Row label="Visibility">
          {corpus.visibility}
          {corpus.owned && (
            <Button
              className="ml-2 py-0.5 px-2 text-xs"
              onClick={async () => {
                try {
                  await api.updateCorpus(corpus.name, { visibility: corpus.visibility === 'shared' ? 'private' : 'shared' });
                  await load();
                  await onRefresh();
                } catch (e) { onError(e); }
              }}
            >
              Make {corpus.visibility === 'shared' ? 'private' : 'shared'}
            </Button>
          )}
        </Row>
        <Row label="Contents">
          {corpus.fileCount.toLocaleString()} files · {corpus.chunkCount.toLocaleString()} chunks
          {corpus.skippedCount > 0 && ` · ${corpus.skippedCount} skipped`}
          {corpus.failedCount > 0 && ` · ${corpus.failedCount} failed`}
        </Row>
      </div>

      <div>
        <div className="flex gap-2 mb-2.5 flex-wrap items-center">
          {/* One-of-N, like the search mode: a status filter is a lens on the same list,
              not five independent buttons. */}
          <Segmented
            label="File status"
            value={filter}
            onChange={setFilter}
            options={[
              { value: '', label: 'all' },
              { value: 'indexed', label: 'indexed' },
              { value: 'skipped', label: 'skipped' },
              { value: 'empty', label: 'empty' },
              { value: 'failed', label: 'failed' },
            ]}
          />
          {problems.length > 0 && <span className="dim text-xs">{problems.length} need attention</span>}
        </div>

        {files.length === 0 ? (
          <Empty title="No files" hint="Run a refresh to index this corpus." />
        ) : (
          <div className="card overflow-hidden">
            {files.slice(0, 300).map((f, i) => (
              <div
                key={f.id}
                className={cn(
                  'flex flex-wrap items-baseline gap-2.5 px-3 py-2',
                  i && 'border-t border-border',
                )}
              >
                {/* The row is the affordance. A file you can see listed and cannot open
                    is the screen telling you it knows something it will not say. */}
                <button
                  type="button"
                  className="mono min-w-[220px] flex-1 cursor-pointer text-left text-xs break-all underline-offset-2 hover:underline focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none"
                  onClick={() => setViewing({ path: f.relativePath })}
                >
                  {f.relativePath}
                </button>
                <Badge tone={stateTone(f.status)}>{f.status}</Badge>
                <span className="dim text-xs">{f.chunkCount} chunks · {formatBytes(f.sizeBytes)}</span>
                {/* This is where "why isn't my PDF searchable" gets answered. */}
                {f.statusDetail && <span className="dim text-xs w-[100%]">↳ {f.statusDetail}</span>}
              </div>
            ))}
          </div>
        )}
      </div>



      <div className="card p-3.5">
        <ChunkSetsPanel corpus={corpus} onChanged={async () => { await load(); await onRefresh(); }} />
      </div>

      {viewing && (
        <FileViewer
          corpus={corpus.name}
          path={viewing.path}
          aroundLine={viewing.line}
          onClose={() => setViewing(null)}
          onError={onError}
        />
      )}

      {addingSource && (
        <AddSourceModal
          corpus={corpus}
          onClose={() => setAddingSource(false)}
          onAdded={async () => { setAddingSource(false); await onRefresh(); }}
          onError={onError}
        />
      )}

      {removingSource && (
        <RemoveSourceModal
          corpus={corpus}
          source={removingSource}
          onClose={() => setRemovingSource(null)}
          onRemoved={async () => { setRemovingSource(null); await load(); }}
          onError={onError}
        />
      )}

      {confirmDelete && (
        <DeleteCorpusModal
          corpus={corpus}
          onClose={() => setConfirmDelete(false)}
          onDeleted={async () => { setConfirmDelete(false); await onRefresh(); onBack(); }}
          onError={onError}
        />
      )}
    </div>
  );
}

/**
 * Add a place this corpus takes content from.
 *
 * Every field here has been in the API since the beginning and in docs/08 since the
 * beginning, and in the UI never, so a corpus was stuck with the one source it was
 * created with, and the filters could not be set or seen at all.
 */
function AddSourceModal({
  corpus,
  onClose,
  onAdded,
  onError,
}: {
  corpus: Corpus;
  onClose: () => void;
  onAdded: () => Promise<void>;
  onError: (e: unknown) => void;
}) {
  const [path, setPath] = useState('');
  const [useGitignore, setUseGitignore] = useState(true);
  const [maxFileMb, setMaxFileMb] = useState(2);
  const [include, setInclude] = useState('');
  const [exclude, setExclude] = useState('');
  const [busy, setBusy] = useState(false);

  /** A comma or newline separated list, with the blanks dropped. */
  const globs = (raw: string) =>
    raw.split(/[\n,]/).map((g) => g.trim()).filter(Boolean);

  const alreadyHere = corpus.sources.some((s) => s.rootPath === path);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    try {
      await api.addSource(corpus.name, {
        workspacePath: path,
        useGitignore,
        maxFileBytes: Math.round(maxFileMb * 1024 * 1024),
        includeGlobs: globs(include),
        excludeGlobs: globs(exclude),
      });
      await onAdded();
    } catch (err) {
      onError(err);
      setBusy(false);
    }
  }

  return (
    <Modal title={`Add a source to ${corpus.name}`} onClose={onClose}>
      <form onSubmit={submit}>
        <Field
          label="Workspace folder"
          hint="Only paths bind-mounted into the container are reachable. Set WORKSPACE_ROOT to change what is available."
        >
          <WorkspacePicker value={path} onChange={setPath} emptyLabel="(choose a folder)" disabled={busy} />
        </Field>

        {alreadyHere && (
          <p className="-mt-2 mb-3 text-xs text-[var(--warn)]">
            This corpus already indexes that folder. Adding it again indexes everything twice.
          </p>
        )}

        <Field label="Largest file (MB)" hint="Anything bigger is skipped and reported, not silently dropped.">
          <Input
            type="number"
            min={0.1}
            step={0.1}
            value={maxFileMb}
            onChange={(e) => setMaxFileMb(Number(e.target.value))}
          />
        </Field>

        <Field label="Only these (optional)" hint="Globs, comma separated. Empty means everything not excluded.">
          <Input className="mono" value={include} onChange={(e) => setInclude(e.target.value)} placeholder="src/**, docs/**" />
        </Field>

        <Field label="Never these (optional)" hint="Globs, comma separated. Applied after the include list.">
          <Input className="mono" value={exclude} onChange={(e) => setExclude(e.target.value)} placeholder="**/vendor/**, *.min.js" />
        </Field>

        <label className="mb-3.5 flex items-start gap-2.5">
          <Checkbox
            checked={useGitignore}
            onCheckedChange={(v) => setUseGitignore(v === true)}
            className="mt-0.5"
          />
          <span className="grid gap-0.5">
            <span className="text-sm leading-none font-semibold">Honour .gitignore</span>
            <span className="text-xs text-muted-foreground">
              And .dexiconignore. Off indexes build output and dependencies too, which is
              almost never what you want.
            </span>
          </span>
        </label>

        <div className="mt-4 flex justify-end gap-2">
          <Button type="button" onClick={onClose}>Cancel</Button>
          <Button variant="primary" type="submit" disabled={!path || busy}>
            {busy ? <Spinner /> : <Plus />}
            Add source
          </Button>
        </div>
      </form>
    </Modal>
  );
}

/**
 * One indexed file, read back.
 *
 * Deliberately NOT the file on disk. This is what was indexed, which is what search is
 * actually searching, which is what to examine when a result is surprising, and the
 * only thing available for an uploaded PDF, which has no file. Where the index is missing
 * lines, the text says so inline rather than closing the gap without notice.
 */
function FileViewer({
  corpus,
  path,
  aroundLine,
  onClose,
  onError,
}: {
  corpus: string;
  path: string;
  aroundLine?: number;
  onClose: () => void;
  onError: (e: unknown) => void;
}) {
  const [file, setFile] = useState<IndexedFileText | null>(null);
  const [more, setMore] = useState(false);
  const highlight = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    api.fileText(corpus, path).then(setFile).catch(onError);
  }, [corpus, path, onError]);

  /**
   * Read on from where the last window stopped.
   *
   * A book is bigger than one response, and the viewer used to stop at 400,000 characters,
   * show a "truncated" badge and offer nothing further, which for a 700-page book meant
   * the first chapter or two and no way to reach the rest.
   */
  async function readOn() {
    if (!file?.nextOffset) return;
    setMore(true);
    try {
      const next = await api.fileText(corpus, path, file.nextOffset);
      setFile({ ...next, startLine: file.startLine, text: file.text + next.text });
    } catch (e) {
      onError(e);
    } finally {
      setMore(false);
    }
  }

  useEffect(() => {
    // Opened from a search hit, so land on the hit rather than at the top of a long file.
    highlight.current?.scrollIntoView({ block: 'center' });
  }, [file]);

  const lines = file?.text.split('\n') ?? [];

  return (
    <Modal title={path} onClose={onClose} width={980}>
      {!file ? (
        <p className="flex items-center gap-2"><Spinner /> Reading…</p>
      ) : (
        <>
          <div className="mb-2.5 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
            <span className="mono">{file.corpus}:{file.chunkSet}</span>
            <span>lines {file.startLine}–{file.endLine}</span>
            {/* A gap means the index really is missing those lines. Saying so is the
                difference between reading a file and reading a plausible one. */}
            {file.gaps > 0 && (
              <Badge tone="warn">{file.gaps} gap{file.gaps === 1 ? '' : 's'} in the index</Badge>
            )}
            {/* How much of the file is on screen. The old badge said "truncated" and
                stopped there, which states a problem and offers no way out of it. */}
            {(file.totalChars ?? 0) > file.text.length && (
              <span>
                {Math.round((100 * file.text.length) / (file.totalChars ?? 1))}% of{' '}
                {(file.totalChars ?? 0).toLocaleString()} characters
              </span>
            )}
            <span className="flex-1" />
            {file.nextOffset != null && (
              <Button size="xs" disabled={more} onClick={readOn}>
                {more ? 'Reading…' : 'Read on'}
              </Button>
            )}
            <CopyButton text={file.text} label="Copy text" />
          </div>

          <pre className="mono m-0 max-h-[62vh] overflow-auto rounded-md bg-muted p-3 text-xs whitespace-pre">
            {lines.map((line, i) => {
              const n = file.startLine + i;
              const isHit = aroundLine !== undefined && n === aroundLine;
              return (
                <span
                  key={i}
                  ref={isHit ? highlight : undefined}
                  className={cn('block', isHit && 'rounded-sm bg-[var(--accent-soft)]')}
                >
                  <span className="mr-3 inline-block w-10 shrink-0 text-right text-muted-foreground select-none">
                    {n}
                  </span>
                  {line}
                </span>
              );
            })}
          </pre>
        </>
      )}
    </Modal>
  );
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex gap-3">
      <span className="dim w-[96px] shrink-0">{label}</span>
      <span className="flex-1">{children}</span>
    </div>
  );
}

function RemoveSourceModal({ corpus, source, onClose, onRemoved, onError }: {
  corpus: Corpus;
  source: Corpus['sources'][number];
  onClose: () => void;
  onRemoved: () => Promise<void>;
  onError: (e: unknown) => void;
}) {
  const [busy, setBusy] = useState(false);
  const where = source.rootPath ?? source.kind;

  return (
    <Modal title={`Remove ${where}?`} onClose={onClose}>
      {/* No typed confirmation, unlike deleting a corpus: this is recoverable by adding
          the folder back, and the cost of getting it wrong is a reindex rather than an
          index that no longer exists. Say what it costs and take one click. */}
      <p className="mt-0 text-sm">
        {source.fileCount
          ? `Its ${source.fileCount.toLocaleString()} files leave the index immediately, in every chunk set of ${corpus.name}.`
          : `It has no indexed files, so nothing leaves the index.`}
        {' '}The folder on disk is untouched; Dexicon only ever reads it. Adding it again
        re-indexes from scratch.
      </p>
      <div className="flex gap-2 justify-end">
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="danger"
          disabled={busy}
          onClick={async () => {
            setBusy(true);
            try { await api.removeSource(corpus.name, source.id); await onRemoved(); }
            catch (e) { onError(e); setBusy(false); }
          }}
        >
          <Trash2 />
          {busy ? 'Removing…' : 'Remove source'}
        </Button>
      </div>
    </Modal>
  );
}

function DeleteCorpusModal({ corpus, onClose, onDeleted, onError }: { corpus: Corpus; onClose: () => void; onDeleted: () => Promise<void>; onError: (e: unknown) => void }) {
  const [typed, setTyped] = useState('');
  return (
    <Modal title={`Delete ${corpus.name}?`} onClose={onClose}>
      <p className="mt-0 text-sm">
        This removes {corpus.chunkCount.toLocaleString()} chunks from the vector store and the corpus from the
        catalogue. The files on disk are untouched. Re-indexing it again may take a while.
      </p>
      {/* Typing the name, because this destroys an index that may have taken an hour. */}
      <Field label={`Type "${corpus.name}" to confirm`}>
        <Input className="font-mono" value={typed} onChange={(e) => setTyped(e.target.value)} autoFocus />
      </Field>
      <div className="flex gap-2 justify-end">
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="danger"
          disabled={typed !== corpus.name}
          onClick={async () => {
            try { await api.deleteCorpus(corpus.name); await onDeleted(); } catch (e) { onError(e); }
          }}
        >
          <Trash2 />
          Delete permanently
        </Button>
      </div>
    </Modal>
  );
}

// ── Jobs ────────────────────────────────────────────────────────────────────

function JobsView({ corpora, live, onError }: { corpora: Corpus[]; live: Record<string, Job & { currentFile?: string }>; onError: (e: unknown) => void }) {
  const [jobs, setJobs] = useState<Job[]>([]);
  const names = useMemo(() => Object.fromEntries(corpora.map((c) => [c.id, c.name])), [corpora]);

  useEffect(() => {
    const load = () => api.listJobs().then(setJobs).catch(onError);
    void load();
    const id = setInterval(load, 4000);
    return () => clearInterval(id);
  }, [onError]);

  if (jobs.length === 0) return <Empty title="No jobs yet" hint="Indexing runs appear here, newest first." />;

  return (
    <div className="grid gap-3">
      <h1 className="m-0 text-lg">Jobs</h1>
      {jobs.map((j) => {
        const merged = live[j.corpusId]?.id === j.id ? live[j.corpusId] : j;
        return (
          <div key={j.id} className="card p-3.5">
            <div className="flex gap-2 items-center flex-wrap">
              <strong>{names[j.corpusId] ?? j.corpusId}</strong>
              <Badge tone={stateTone(merged.state)}>{merged.state}</Badge>
              <Badge>{j.kind}</Badge>
              <span className="flex-1" />
              <span className="dim text-xs">
                {j.finishedUtc
                  ? `finished ${relativeTime(j.finishedUtc)}`
                  : j.startedUtc
                    ? `started ${relativeTime(j.startedUtc)}`
                    // A job waiting behind a long one says how long it has waited. "queued"
                    // alone cannot distinguish a fresh enqueue from one stuck for an hour.
                    : `queued ${relativeTime(j.queuedUtc)}`}
              </span>
            </div>

            {(merged.state === 'running' || merged.phase) && <ProgressBar job={merged} />}

            <div className="dim mt-1.5 text-xs flex gap-3.5 flex-wrap">
              <span>{merged.filesDone.toLocaleString()} indexed</span>
              <span>{merged.filesSkipped.toLocaleString()} skipped</span>
              {merged.filesFailed > 0 && <span className="text-[var(--danger)]">{merged.filesFailed.toLocaleString()} failed</span>}
              <span>{merged.chunksWritten.toLocaleString()} chunks</span>
            </div>

            {merged.error && (
              <p
                className={cn(
                  'mt-2 mb-0 text-xs',
                  // A job that is alive but achieving nothing is not a failed one, and
                  // colouring it red says it is.
                  merged.state === 'degraded' ? 'text-[var(--warn)]' : 'text-[var(--danger)]',
                )}
              >
                {merged.error}
              </p>
            )}
          </div>
        );
      })}
    </div>
  );
}

// ── Access ──────────────────────────────────────────────────────────────────

function AccessView({ onError }: { onError: (e: unknown) => void }) {
  const [tokens, setTokens] = useState<Awaited<ReturnType<typeof api.listTokens>>>([]);
  const [tenants, setTenants] = useState<Awaited<ReturnType<typeof api.listTenants>>>([]);
  const [creating, setCreating] = useState(false);
  const [issued, setIssued] = useState<Awaited<ReturnType<typeof api.createToken>> | null>(null);

  const load = useCallback(async () => {
    try {
      setTokens(await api.listTokens());
      setTenants(await api.listTenants());
    } catch (e) { onError(e); }
  }, [onError]);

  useEffect(() => { void load(); }, [load]);

  return (
    <div className="grid gap-5">
      <section>
        <div className="flex justify-between items-center mb-3">
          <h1 className="m-0 text-lg">Tokens</h1>
          <Button variant="primary" onClick={() => setCreating(true)}><Plus />New token</Button>
        </div>

        {tokens.length === 0 ? (
          <Empty title="No tokens" hint="A token is the only credential. Create one to connect an agent." />
        ) : (
          <div className="card">
            {tokens.map((t, i) => (
              <div
                key={t.id}
                className={cn('flex flex-wrap items-center gap-2.5 px-3 py-2.5', i && 'border-t border-border')}
              >
                <strong className="text-sm">{t.name}</strong>
                <Badge>{t.scopes}</Badge>
                {t.revokedUtc && <Badge tone="danger">revoked</Badge>}
                {t.expiresUtc && new Date(t.expiresUtc) < new Date() && <Badge tone="warn">expired</Badge>}
                <span className="flex-1" />
                <span className="dim text-xs" title={localTime(t.lastUsedUtc)}>used {relativeTime(t.lastUsedUtc)}</span>
                {!t.revokedUtc && (
                  <Button variant="danger" onClick={async () => { try { await api.revokeToken(t.id); await load(); } catch (e) { onError(e); } }}>
                    Revoke
                  </Button>
                )}
              </div>
            ))}
          </div>
        )}
        <p className="dim text-xs mt-2">
          Secrets are shown once, at creation, and are stored only as a PBKDF2 hash. There is no way to recover one.
        </p>
      </section>

      <section>
        <h2 className="mt-0 mx-0 mb-3 text-base">Tenants</h2>
        <div className="card">
          {tenants.map((t, i: number) => (
            <div
              key={t.id}
              className={cn('flex items-center gap-2.5 px-3 py-2.5', i && 'border-t border-border')}
            >
              <code className="mono text-sm">{t.id}</code>
              {t.disabled && <Badge tone="danger">disabled</Badge>}
              <span className="flex-1" />
              <span className="dim text-xs" title={localTime(t.createdUtc)}>created {relativeTime(t.createdUtc)}</span>
            </div>
          ))}
        </div>
        <p className="dim text-xs mt-2">
          A tenant is the isolation boundary. Sharing a corpus grants read access only; writes are always owner-only.
        </p>
      </section>

      {creating && (
        <CreateTokenModal
          onClose={() => setCreating(false)}
          onCreated={async (t) => { setCreating(false); setIssued(t); await load(); }}
          onError={onError}
        />
      )}

      {issued && (
        <Modal title="Token created" onClose={() => setIssued(null)} width={680}>
          <p className="mt-0 text-sm text-[var(--warn)]">
            <strong>Copy it now.</strong> This is the only time it will be shown. It is stored as a hash and cannot be recovered.
          </p>
          <pre className="mono overflow-x-auto rounded-md bg-muted p-3 text-xs break-all whitespace-pre-wrap">
            {issued.secret}
          </pre>
          <div className="flex gap-2 mb-4">
            <CopyButton text={issued.secret} label="Copy token" />
          </div>

          {/* The step between "installed" and "working". */}
          <p className="text-xs font-semibold mt-0 mx-0 mb-1.5">Connect an agent</p>
          <pre className="mono overflow-x-auto rounded-md bg-muted p-3 text-xs break-all whitespace-pre-wrap">
            {issued.mcpAddCommand}
          </pre>
          <CopyButton text={issued.mcpAddCommand} label="Copy command" />
        </Modal>
      )}
    </div>
  );
}

function CreateTokenModal({ onClose, onCreated, onError }: { onClose: () => void; onCreated: (t: Awaited<ReturnType<typeof api.createToken>>) => Promise<void>; onError: (e: unknown) => void }) {
  const [name, setName] = useState('');
  const [scopes, setScopes] = useState<string[]>(['search']);
  const [busy, setBusy] = useState(false);

  const toggle = (s: string) => setScopes((prev) => (prev.includes(s) ? prev.filter((x) => x !== s) : [...prev, s]));

  return (
    <Modal title="New token" onClose={onClose}>
      <form
        onSubmit={async (e) => {
          e.preventDefault();
          setBusy(true);
          try { await onCreated(await api.createToken(name, scopes)); }
          catch (err) { onError(err); setBusy(false); }
        }}
      >
        <Field label="Name" hint="What this token is for. It appears in the audit log.">
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="claude-code" autoFocus />
        </Field>
        <Field label="Scopes" hint="search reads; ingest can trigger reindexing; admin manages tenants, corpora and tokens.">
          <div className="flex gap-1.5 flex-wrap">
            {['search', 'ingest', 'admin'].map((s) => (
              <Chip key={s} type="button" active={scopes.includes(s)} onClick={() => toggle(s)}>
                {scopes.includes(s) && <Check />}
                {s}
              </Chip>
            ))}
          </div>
        </Field>
        <div className="flex gap-2 justify-end mt-4">
          <Button type="button" onClick={onClose}>Cancel</Button>
          <Button variant="primary" type="submit" disabled={!name.trim() || scopes.length === 0 || busy}>
            {busy ? <Spinner /> : null} Create
          </Button>
        </div>
      </form>
    </Modal>
  );
}

// ── Settings ────────────────────────────────────────────────────────────────

function SettingsView({ health }: { health: Health | null }) {
  const [theme, setTheme] = useState<string>(() => document.documentElement.dataset.theme ?? 'auto');

  // The dots in the header are a 15-second poll and keep their last known value on a
  // failure, which is right for a status light and wrong for "is it working NOW". This
  // asks, once, and reports what came back, including how long it took, because a
  // dependency that answers in eight seconds is a different problem from one that does
  // not answer at all.
  const [checking, setChecking] = useState(false);
  const [checked, setChecked] = useState<{ health: Health; tookMs: number } | null>(null);
  const [checkError, setCheckError] = useState<unknown>(null);

  const checkConnectivity = async () => {
    setChecking(true);
    setCheckError(null);
    const started = performance.now();
    try {
      setChecked({ health: await api.health(), tookMs: Math.round(performance.now() - started) });
    } catch (e) {
      setCheckError(e);
      setChecked(null);
    } finally {
      setChecking(false);
    }
  };

  useEffect(() => {
    if (theme === 'auto') delete document.documentElement.dataset.theme;
    else document.documentElement.dataset.theme = theme;
    try { localStorage.setItem('dexicon.theme', theme); } catch { /* blocked storage is fine */ }
  }, [theme]);

  return (
    <div className="grid gap-5 max-w-[680px]">
      <h1 className="m-0 text-lg">Settings</h1>

      <section className="card p-3.5">
        <h2 className="mt-0 mx-0 mb-3 text-sm">Appearance</h2>
        <div className="flex gap-1.5">
          {['auto', 'light', 'dark'].map((t) => (
            <Chip key={t} active={theme === t} onClick={() => setTheme(t)}>
              {t}
            </Chip>
          ))}
        </div>
      </section>

      <section className="card p-3.5">
        <h2 className="mt-0 mx-0 mb-3 text-sm">Dependencies</h2>
        {/* Read-only: endpoints come from the environment. Making them editable here
            would mean storing them, and an endpoint is not a user preference. */}
        <div className="grid gap-2 text-sm">
          <Row label="Qdrant"><span className="mono">{health?.qdrant.endpoint ?? '—'}</span> <Badge tone={health?.qdrant.reachable ? 'ok' : 'danger'}>{health?.qdrant.reachable ? 'reachable' : 'unreachable'}</Badge></Row>
          <Row label="Ollama"><span className="mono">{health?.ollama.endpoint ?? '—'}</span> <Badge tone={health?.ollama.reachable ? 'ok' : 'danger'}>{health?.ollama.reachable ? 'reachable' : 'unreachable'}</Badge></Row>
          <Row label="Model"><span className="mono">{health?.ollama.model ?? '—'}</span> · {health?.ollama.dimensions ?? 0}d</Row>
          <Row label="Corpora">{health?.corpora ?? 0}</Row>
        </div>

        <div className="mt-3">
          <Button disabled={checking} onClick={() => void checkConnectivity()}>
            {checking ? <Spinner /> : 'Check connectivity'}
          </Button>

          <ErrorBanner error={checkError} onDismiss={() => setCheckError(null)} />

          {checked && (
            <div className="mt-2.5 text-sm grid gap-1">
              <Row label="Checked">
                {localTime(new Date().toISOString())} · {checked.tookMs} ms
              </Row>
              <Row label="Qdrant">
                <Badge tone={checked.health.qdrant.reachable ? 'ok' : 'danger'}>
                  {checked.health.qdrant.reachable ? 'answered' : 'no answer'}
                </Badge>
              </Row>
              <Row label="Embeddings">
                <Badge tone={checked.health.ollama.reachable ? 'ok' : 'danger'}>
                  {checked.health.ollama.reachable ? 'answered' : 'no answer'}
                </Badge>{' '}
                {checked.health.ollama.error && (
                  <span className="dim text-xs">{checked.health.ollama.error}</span>
                )}
              </Row>
              {!checked.health.ollama.reachable && (
                <p className="dim m-0 text-xs">
                  Search still works in <strong>keyword</strong> mode without embeddings, and says so in the
                  response. Indexing will retry and report the files it could not embed.
                </p>
              )}
            </div>
          )}
        </div>
        <p className="dim text-xs mt-3 mx-0 mb-0">
          These come from the environment (DEXICON__QDRANT__ENDPOINT, DEXICON__OLLAMA__ENDPOINT) and are shown read-only.
        </p>
      </section>
    </div>
  );
}
