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
  type Job,
  type SearchResult,
} from './api';
import { Badge, CopyButton, Empty, ErrorBanner, Field, Modal, Spinner, formatBytes, localTime, relativeTime, stateTone } from './ui';
import { DocumentsView } from './Documents';
import { ChunkSetsPanel, ModelsView } from './ChunkSets';

type View = 'search' | 'corpora' | 'documents' | 'jobs' | 'models' | 'access' | 'settings';

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
    <div style={{ display: 'grid', placeItems: 'center', minHeight: '100dvh', padding: '1rem' }}>
      <form onSubmit={submit} className="card" style={{ padding: '1.5rem', width: '100%', maxWidth: 480 }}>
        <h1 style={{ margin: '0 0 0.25rem', fontSize: '1.3rem' }}>Dexicon</h1>
        <p className="dim" style={{ margin: '0 0 1.2rem', fontSize: '0.875rem' }}>
          Paste an API token to continue.
        </p>

        <Field
          label="API token"
          hint="On a fresh install the bootstrap token is printed once in the container log: docker compose logs dexicon | grep bootstrap"
        >
          <input
            className="input mono"
            type="password"
            autoComplete="off"
            placeholder="dex_…"
            value={value}
            onChange={(e) => setValue(e.target.value)}
          />
        </Field>

        {error != null && <div style={{ marginBottom: '0.9rem' }}><ErrorBanner error={error} /></div>}

        <button className="btn btn-primary" type="submit" disabled={!value.trim() || busy} style={{ width: '100%', justifyContent: 'center' }}>
          {busy ? <Spinner /> : null} Continue
        </button>
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
        // perfectly normal work — a false alarm is as bad as a missed one. Keep the
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

  const nav: { id: View; label: string }[] = [
    { id: 'search', label: 'Search' },
    { id: 'corpora', label: 'Corpora' },
    { id: 'documents', label: 'Documents' },
    { id: 'jobs', label: 'Jobs' },
    { id: 'models', label: 'Models' },
    { id: 'access', label: 'Access' },
    { id: 'settings', label: 'Settings' },
  ];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', minHeight: '100dvh' }}>
      <header
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: '1rem',
          padding: '0.6rem 1rem',
          borderBottom: '1px solid var(--border)',
          background: 'var(--surface)',
          flexWrap: 'wrap',
        }}
      >
        <strong style={{ fontSize: '1.02rem' }}>Dexicon</strong>

        <nav style={{ display: 'flex', gap: '0.25rem', flex: 1, flexWrap: 'wrap' }}>
          {nav.map((n) => (
            <button
              key={n.id}
              className="btn"
              aria-current={view === n.id ? 'page' : undefined}
              onClick={() => { setView(n.id); setSelected(null); }}
              style={
                view === n.id
                  ? { background: 'var(--accent-soft)', borderColor: 'color-mix(in oklab, var(--accent) 35%, transparent)', color: 'var(--accent)' }
                  : { borderColor: 'transparent', background: 'transparent' }
              }
            >
              {n.label}
            </button>
          ))}
        </nav>

        <HealthDots health={health} connected={connected} stale={healthStale} />
        <button className="btn" onClick={onSignOut}>Sign out</button>
      </header>

      <main style={{ flex: 1, padding: '1.1rem', maxWidth: 1180, width: '100%', margin: '0 auto' }}>
        {error != null && (
          <div style={{ marginBottom: '1rem' }}>
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
    <div style={{ position: 'relative' }}>
      <button className="btn" onClick={() => setOpen((v) => !v)} aria-expanded={open} title="Dependency health">
        <span style={dot(health?.qdrant.reachable)} /> qdrant
        <span style={{ ...dot(health?.ollama.reachable), marginLeft: 6 }} /> ollama
        {stale && <span className="dim" style={{ marginLeft: 6, fontSize: '0.75rem' }}>· checking</span>}
        {!connected && <span className="dim" style={{ marginLeft: 6, fontSize: '0.75rem' }}>· reconnecting</span>}
      </button>

      {open && health && (
        <div className="card" style={{ position: 'absolute', right: 0, top: '2.4rem', padding: '0.8rem', width: 330, zIndex: 20, fontSize: '0.8rem' }}>
          <p style={{ margin: '0 0 0.5rem', fontWeight: 650 }}>Qdrant</p>
          <p className="dim mono" style={{ margin: 0, wordBreak: 'break-all' }}>{health.qdrant.endpoint}</p>
          <p style={{ margin: '0.2rem 0 0.8rem' }}>
            <Badge tone={health.qdrant.reachable ? 'ok' : 'danger'}>{health.qdrant.reachable ? 'reachable' : 'unreachable'}</Badge>
          </p>

          <p style={{ margin: '0 0 0.5rem', fontWeight: 650 }}>Ollama</p>
          <p className="dim mono" style={{ margin: 0, wordBreak: 'break-all' }}>{health.ollama.endpoint}</p>
          <p style={{ margin: '0.2rem 0 0' }}>
            <Badge tone={health.ollama.reachable ? 'ok' : 'danger'}>{health.ollama.model}</Badge>{' '}
            <Badge>{health.ollama.dimensions}d</Badge>
          </p>
          {health.ollama.error && (
            <p style={{ margin: '0.5rem 0 0', color: 'var(--danger)' }}>{health.ollama.error}</p>
          )}
          {!health.ollama.reachable && (
            <p className="dim" style={{ margin: '0.5rem 0 0' }}>
              Search still works in keyword mode. Indexing will back off until it recovers.
            </p>
          )}
        </div>
      )}
    </div>
  );
}

// ── Search ──────────────────────────────────────────────────────────────────

function SearchView({ corpora, onError }: { corpora: Corpus[]; onError: (e: unknown) => void }) {
  const [query, setQuery] = useState('');
  const [mode, setMode] = useState<'hybrid' | 'semantic' | 'keyword'>('hybrid');
  const [scope, setScope] = useState<string[]>([]);
  const [limit, setLimit] = useState(10);
  const [result, setResult] = useState<SearchResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [explain, setExplain] = useState(false);
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
    <div style={{ display: 'grid', gap: '1rem' }}>
      <form onSubmit={run} className="card" style={{ padding: '1rem', display: 'grid', gap: '0.75rem' }}>
        <input
          ref={inputRef}
          className="input"
          placeholder="Ask a question, or paste a code fragment…   (press / to focus)"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          style={{ fontSize: '0.95rem', padding: '0.6rem 0.8rem' }}
        />

        <div style={{ display: 'flex', gap: '0.75rem', flexWrap: 'wrap', alignItems: 'center' }}>
          <div style={{ display: 'flex', gap: 2 }} role="radiogroup" aria-label="Search mode">
            {(['hybrid', 'semantic', 'keyword'] as const).map((m) => (
              <button
                key={m}
                type="button"
                role="radio"
                aria-checked={mode === m}
                className="btn"
                onClick={() => setMode(m)}
                style={mode === m ? { background: 'var(--accent-soft)', color: 'var(--accent)', borderColor: 'color-mix(in oklab, var(--accent) 35%, transparent)' } : undefined}
              >
                {m}
              </button>
            ))}
          </div>

          <select
            className="input"
            style={{ width: 'auto', minWidth: 170 }}
            value={scope.length === 1 ? scope[0] : ''}
            onChange={(e) => setScope(e.target.value ? [e.target.value] : [])}
            aria-label="Corpus scope"
          >
            <option value="">All visible corpora</option>
            {corpora.map((c) => (
              <option key={c.id} value={c.name}>{c.name}</option>
            ))}
          </select>

          <select className="input" style={{ width: 'auto' }} value={limit} onChange={(e) => setLimit(Number(e.target.value))} aria-label="Result limit">
            {[5, 10, 20, 50].map((n) => <option key={n} value={n}>{n} results</option>)}
          </select>

          <button className="btn btn-primary" type="submit" disabled={busy || !query.trim()}>
            {busy ? <Spinner /> : null} Search
          </button>
        </div>
      </form>

      {result?.degraded && (
        <div className="card" style={{ padding: '0.7rem 0.9rem', borderColor: 'color-mix(in oklab, var(--warn) 45%, transparent)', background: 'color-mix(in oklab, var(--warn) 8%, transparent)', fontSize: '0.875rem' }}>
          <strong>Degraded:</strong> {result.degradedReason}
        </div>
      )}
      {result?.note && (
        <div className="card" style={{ padding: '0.7rem 0.9rem', borderColor: 'color-mix(in oklab, var(--accent) 40%, transparent)', fontSize: '0.875rem' }}>
          {result.note}
        </div>
      )}

      {result && (
        <>
          <div style={{ display: 'flex', alignItems: 'center', gap: '0.6rem', fontSize: '0.82rem', flexWrap: 'wrap' }}>
            <span className="dim">
              {result.hits.length} result{result.hits.length === 1 ? '' : 's'} · {result.tookMs} ms
            </span>
            <button className="btn" onClick={() => setExplain((v) => !v)} style={{ padding: '0.15rem 0.5rem', fontSize: '0.75rem' }}>
              {explain ? 'Hide' : 'Explain'}
            </button>
          </div>

          {/* How you tell "the index is bad" from "the scope was wrong" — the single
              most useful thing this screen can show. */}
          {explain && (
            <div className="card" style={{ padding: '0.8rem', fontSize: '0.8rem', display: 'grid', gap: '0.3rem' }}>
              <div><span className="dim">resolved scope:</span> {result.scope.map((s) => s.name).join(', ') || '(none)'}</div>
              <div><span className="dim">mode used:</span> {result.mode}{result.degraded ? ' (degraded from requested)' : ''}</div>
              <div><span className="dim">scores:</span> reciprocal rank fusion, k=2 — ordering is meaningful, magnitude is not</div>
            </div>
          )}

          {result.hits.length === 0 ? (
            <Empty
              title="Nothing matched"
              hint="If that is unexpected, check Jobs — the corpus may still be indexing, or the content may not be indexed at all."
            />
          ) : (
            <div style={{ display: 'grid', gap: '0.7rem' }}>
              {result.hits.map((h, i) => (
                <article key={`${h.corpusId}-${h.filePath}-${h.startLine}-${i}`} className="card" style={{ padding: '0.85rem' }}>
                  <header style={{ display: 'flex', gap: '0.6rem', alignItems: 'baseline', flexWrap: 'wrap', marginBottom: '0.5rem' }}>
                    <code className="mono" style={{ fontSize: '0.86rem', fontWeight: 600 }}>{h.location}</code>
                    {h.section && <span className="dim" style={{ fontSize: '0.8rem' }}>· {h.section}</span>}
                    <span style={{ flex: 1 }} />
                    {h.language && <Badge>{h.language}</Badge>}
                    {result.scope.length > 1 && h.corpusName && <Badge tone="accent">{h.corpusName}</Badge>}
                    <CopyButton text={h.location ?? ''} label="Copy path" />
                  </header>
                  <pre
                    className="mono"
                    style={{
                      margin: 0,
                      padding: '0.6rem 0.7rem',
                      background: 'var(--surface-2)',
                      borderRadius: 7,
                      fontSize: '0.79rem',
                      overflowX: 'auto',
                      whiteSpace: 'pre-wrap',
                      wordBreak: 'break-word',
                      maxHeight: 340,
                    }}
                  >
                    {h.content}
                  </pre>
                </article>
              ))}
            </div>
          )}
        </>
      )}

      {!result && (
        <Empty
          title="Search your indexed content"
          hint={corpora.length === 0 ? 'No corpora yet — create one under Corpora first.' : 'Hybrid blends meaning with exact terms. Keyword works even when embeddings are down.'}
        />
      )}
    </div>
  );
}

// ── Corpora ─────────────────────────────────────────────────────────────────

function CorporaView({
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
    <div style={{ display: 'grid', gap: '1rem' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1 style={{ margin: 0, fontSize: '1.15rem' }}>Corpora</h1>
        <button className="btn btn-primary" onClick={() => setCreating(true)}>New corpus</button>
      </div>

      {corpora.length === 0 ? (
        <Empty
          title="No corpora yet"
          hint="A corpus is a named body of content: a repository, a folder of docs, a PDF library."
          action={<button className="btn btn-primary" onClick={() => setCreating(true)}>Create the first one</button>}
        />
      ) : (
        <div style={{ display: 'grid', gap: '0.7rem' }}>
          {corpora.map((c) => {
            const job = live[c.id];
            const running = job && (job.state === 'running' || job.phase);
            return (
              <button key={c.id} className="card" onClick={() => onOpen(c.name)} style={{ padding: '0.9rem', textAlign: 'left', cursor: 'pointer', border: '1px solid var(--border)' }}>
                <div style={{ display: 'flex', gap: '0.55rem', alignItems: 'center', flexWrap: 'wrap' }}>
                  <strong>{c.name}</strong>
                  <Badge tone={stateTone(c.state)}>{c.state}</Badge>
                  {c.visibility === 'shared' && <Badge tone="accent">shared</Badge>}
                  {!c.owned && <Badge>read-only</Badge>}
                  <span style={{ flex: 1 }} />
                  <span className="dim" style={{ fontSize: '0.78rem' }} title={localTime(c.lastIndexedUtc)}>indexed {relativeTime(c.lastIndexedUtc)}</span>
                </div>

                {c.description && <p className="dim" style={{ margin: '0.35rem 0 0', fontSize: '0.83rem' }}>{c.description}</p>}

                <div className="dim" style={{ marginTop: '0.45rem', fontSize: '0.8rem', display: 'flex', gap: '0.85rem', flexWrap: 'wrap' }}>
                  <span>{c.fileCount.toLocaleString()} files</span>
                  <span>{c.chunkCount.toLocaleString()} chunks</span>
                  {c.skippedCount > 0 && <span>{c.skippedCount.toLocaleString()} skipped</span>}
                  {c.failedCount > 0 && <span style={{ color: 'var(--danger)' }}>{c.failedCount.toLocaleString()} failed</span>}
                  {/* The default set is what this corpus answers to unqualified. */}
                  <span className="mono">
                    {c.chunkSets.find((s) => s.isDefault)?.embeddingModel ?? c.chunkSets[0]?.embeddingModel ?? '—'}
                  </span>
                  {c.chunkSets.length > 1 && <span>{c.chunkSets.length} chunk sets</span>}
                </div>

                {running && <ProgressBar job={job} />}
              </button>
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
    <div style={{ marginTop: '0.6rem' }}>
      <div style={{ height: 5, background: 'var(--surface-2)', borderRadius: 999, overflow: 'hidden' }}>
        <div style={{ width: `${pct}%`, height: '100%', background: 'var(--accent)', transition: 'width 300ms ease' }} />
      </div>
      <div className="dim" style={{ fontSize: '0.75rem', marginTop: '0.3rem' }}>
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
  const [entries, setEntries] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    // Browse the mount so nobody can type a path that does not exist.
    api.browse().then((l) => setEntries(l.entries.filter((e) => e.isDirectory).map((e) => e.relativePath))).catch(() => setEntries([]));
  }, []);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    try {
      await api.createCorpus({ name, description: description || undefined, workspacePath: path || undefined });
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
          <input className="input" value={name} onChange={(e) => setName(e.target.value)} placeholder="api-repo" autoFocus />
        </Field>
        <Field label="Description (optional)">
          <input className="input" value={description} onChange={(e) => setDescription(e.target.value)} />
        </Field>
        <Field label="Workspace folder" hint="Only paths bind-mounted into the container are listed. Set WORKSPACE_ROOT to change what is available.">
          <select className="input" value={path} onChange={(e) => setPath(e.target.value)}>
            <option value="">(add a source later)</option>
            {entries.map((p) => <option key={p} value={p}>{p}</option>)}
          </select>
        </Field>
        <div style={{ display: 'flex', gap: '0.5rem', justifyContent: 'flex-end', marginTop: '1rem' }}>
          <button type="button" className="btn" onClick={onClose}>Cancel</button>
          <button type="submit" className="btn btn-primary" disabled={!name.trim() || busy}>
            {busy ? <Spinner /> : null} Create
          </button>
        </div>
      </form>
    </Modal>
  );
}

function CorpusDetail({
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
    <div style={{ display: 'grid', gap: '1rem' }}>
      <div style={{ display: 'flex', gap: '0.6rem', alignItems: 'center', flexWrap: 'wrap' }}>
        <button className="btn" onClick={onBack}>← Corpora</button>
        <h1 style={{ margin: 0, fontSize: '1.15rem' }}>{corpus.name}</h1>
        <Badge tone={stateTone(corpus.state)}>{corpus.state}</Badge>
        <span style={{ flex: 1 }} />
        {corpus.owned && (
          <>
            <button className="btn" onClick={async () => { try { await api.reindex(corpus.name); } catch (e) { onError(e); } }}>Refresh</button>
            <button className="btn" onClick={async () => { try { await api.reindex(corpus.name, true); } catch (e) { onError(e); } }}>Full reindex</button>
            <button className="btn btn-danger" onClick={() => setConfirmDelete(true)}>Delete</button>
          </>
        )}
      </div>

      {job?.phase && <div className="card" style={{ padding: '0.85rem' }}><ProgressBar job={job} /></div>}

      <div className="card" style={{ padding: '0.9rem', display: 'grid', gap: '0.45rem', fontSize: '0.85rem' }}>
        <Row label="Sources">{corpus.sources.map((s) => s.rootPath ?? s.kind).join(', ') || '—'}</Row>
        <Row label="Searched as">
          <span className="mono">{corpus.name}</span>
          <span className="dim">
            {' '}— the default set below. Name another with <span className="mono">corpus:set</span>.
          </span>
        </Row>
        <Row label="Visibility">
          {corpus.visibility}
          {corpus.owned && (
            <button
              className="btn"
              style={{ marginLeft: '0.5rem', padding: '0.1rem 0.45rem', fontSize: '0.75rem' }}
              onClick={async () => {
                try {
                  await api.updateCorpus(corpus.name, { visibility: corpus.visibility === 'shared' ? 'private' : 'shared' });
                  await load();
                  await onRefresh();
                } catch (e) { onError(e); }
              }}
            >
              Make {corpus.visibility === 'shared' ? 'private' : 'shared'}
            </button>
          )}
        </Row>
        <Row label="Contents">
          {corpus.fileCount.toLocaleString()} files · {corpus.chunkCount.toLocaleString()} chunks
          {corpus.skippedCount > 0 && ` · ${corpus.skippedCount} skipped`}
          {corpus.failedCount > 0 && ` · ${corpus.failedCount} failed`}
        </Row>
      </div>

      <div>
        <div style={{ display: 'flex', gap: '0.4rem', marginBottom: '0.6rem', flexWrap: 'wrap' }}>
          {['', 'indexed', 'skipped', 'empty', 'failed'].map((s) => (
            <button key={s || 'all'} className="btn" onClick={() => setFilter(s)} style={filter === s ? { background: 'var(--accent-soft)', color: 'var(--accent)' } : undefined}>
              {s || 'all'}
            </button>
          ))}
          {problems.length > 0 && <span className="dim" style={{ alignSelf: 'center', fontSize: '0.8rem' }}>{problems.length} need attention</span>}
        </div>

        {files.length === 0 ? (
          <Empty title="No files" hint="Run a refresh to index this corpus." />
        ) : (
          <div className="card" style={{ overflow: 'hidden' }}>
            {files.slice(0, 300).map((f, i) => (
              <div key={f.id} style={{ padding: '0.5rem 0.75rem', borderTop: i ? '1px solid var(--border)' : undefined, display: 'flex', gap: '0.6rem', alignItems: 'baseline', flexWrap: 'wrap' }}>
                <code className="mono" style={{ fontSize: '0.79rem', flex: 1, minWidth: 220, wordBreak: 'break-all' }}>{f.relativePath}</code>
                <Badge tone={stateTone(f.status)}>{f.status}</Badge>
                <span className="dim" style={{ fontSize: '0.75rem' }}>{f.chunkCount} chunks · {formatBytes(f.sizeBytes)}</span>
                {/* This is where "why isn't my PDF searchable" gets answered. */}
                {f.statusDetail && <span className="dim" style={{ fontSize: '0.75rem', width: '100%' }}>↳ {f.statusDetail}</span>}
              </div>
            ))}
          </div>
        )}
      </div>



      <div className="card" style={{ padding: '0.9rem' }}>
        <ChunkSetsPanel corpus={corpus} onChanged={async () => { await load(); await onRefresh(); }} />
      </div>

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

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', gap: '0.75rem' }}>
      <span className="dim" style={{ width: 96, flexShrink: 0 }}>{label}</span>
      <span style={{ flex: 1 }}>{children}</span>
    </div>
  );
}

function DeleteCorpusModal({ corpus, onClose, onDeleted, onError }: { corpus: Corpus; onClose: () => void; onDeleted: () => Promise<void>; onError: (e: unknown) => void }) {
  const [typed, setTyped] = useState('');
  return (
    <Modal title={`Delete ${corpus.name}?`} onClose={onClose}>
      <p style={{ marginTop: 0, fontSize: '0.875rem' }}>
        This removes {corpus.chunkCount.toLocaleString()} chunks from the vector store and the corpus from the
        catalogue. The files on disk are untouched. Re-indexing it again may take a while.
      </p>
      {/* Typing the name, because this destroys an index that may have taken an hour. */}
      <Field label={`Type "${corpus.name}" to confirm`}>
        <input className="input mono" value={typed} onChange={(e) => setTyped(e.target.value)} autoFocus />
      </Field>
      <div style={{ display: 'flex', gap: '0.5rem', justifyContent: 'flex-end' }}>
        <button className="btn" onClick={onClose}>Cancel</button>
        <button
          className="btn btn-danger"
          disabled={typed !== corpus.name}
          onClick={async () => {
            try { await api.deleteCorpus(corpus.name); await onDeleted(); } catch (e) { onError(e); }
          }}
        >
          Delete permanently
        </button>
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
    <div style={{ display: 'grid', gap: '0.7rem' }}>
      <h1 style={{ margin: 0, fontSize: '1.15rem' }}>Jobs</h1>
      {jobs.map((j) => {
        const merged = live[j.corpusId]?.id === j.id ? live[j.corpusId] : j;
        return (
          <div key={j.id} className="card" style={{ padding: '0.85rem' }}>
            <div style={{ display: 'flex', gap: '0.55rem', alignItems: 'center', flexWrap: 'wrap' }}>
              <strong>{names[j.corpusId] ?? j.corpusId}</strong>
              <Badge tone={stateTone(merged.state)}>{merged.state}</Badge>
              <Badge>{j.kind}</Badge>
              <span style={{ flex: 1 }} />
              <span className="dim" style={{ fontSize: '0.78rem' }}>
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

            <div className="dim" style={{ marginTop: '0.4rem', fontSize: '0.8rem', display: 'flex', gap: '0.85rem', flexWrap: 'wrap' }}>
              <span>{merged.filesDone.toLocaleString()} indexed</span>
              <span>{merged.filesSkipped.toLocaleString()} skipped</span>
              {merged.filesFailed > 0 && <span style={{ color: 'var(--danger)' }}>{merged.filesFailed.toLocaleString()} failed</span>}
              <span>{merged.chunksWritten.toLocaleString()} chunks</span>
            </div>

            {merged.error && (
              <p style={{ margin: '0.5rem 0 0', fontSize: '0.8rem', color: merged.state === 'degraded' ? 'var(--warn)' : 'var(--danger)' }}>
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
    <div style={{ display: 'grid', gap: '1.2rem' }}>
      <section>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '0.7rem' }}>
          <h1 style={{ margin: 0, fontSize: '1.15rem' }}>Tokens</h1>
          <button className="btn btn-primary" onClick={() => setCreating(true)}>New token</button>
        </div>

        {tokens.length === 0 ? (
          <Empty title="No tokens" hint="A token is the only credential. Create one to connect an agent." />
        ) : (
          <div className="card">
            {tokens.map((t, i) => (
              <div key={t.id} style={{ padding: '0.6rem 0.8rem', borderTop: i ? '1px solid var(--border)' : undefined, display: 'flex', gap: '0.6rem', alignItems: 'center', flexWrap: 'wrap' }}>
                <strong style={{ fontSize: '0.88rem' }}>{t.name}</strong>
                <Badge>{t.scopes}</Badge>
                {t.revokedUtc && <Badge tone="danger">revoked</Badge>}
                {t.expiresUtc && new Date(t.expiresUtc) < new Date() && <Badge tone="warn">expired</Badge>}
                <span style={{ flex: 1 }} />
                <span className="dim" style={{ fontSize: '0.78rem' }} title={localTime(t.lastUsedUtc)}>used {relativeTime(t.lastUsedUtc)}</span>
                {!t.revokedUtc && (
                  <button className="btn btn-danger" onClick={async () => { try { await api.revokeToken(t.id); await load(); } catch (e) { onError(e); } }}>
                    Revoke
                  </button>
                )}
              </div>
            ))}
          </div>
        )}
        <p className="dim" style={{ fontSize: '0.78rem', marginTop: '0.5rem' }}>
          Secrets are shown once, at creation, and are stored only as a PBKDF2 hash. There is no way to recover one.
        </p>
      </section>

      <section>
        <h2 style={{ margin: '0 0 0.7rem', fontSize: '1rem' }}>Tenants</h2>
        <div className="card">
          {tenants.map((t, i: number) => (
            <div key={t.id} style={{ padding: '0.6rem 0.8rem', borderTop: i ? '1px solid var(--border)' : undefined, display: 'flex', gap: '0.6rem', alignItems: 'center' }}>
              <code className="mono" style={{ fontSize: '0.85rem' }}>{t.id}</code>
              {t.disabled && <Badge tone="danger">disabled</Badge>}
              <span style={{ flex: 1 }} />
              <span className="dim" style={{ fontSize: '0.78rem' }} title={localTime(t.createdUtc)}>created {relativeTime(t.createdUtc)}</span>
            </div>
          ))}
        </div>
        <p className="dim" style={{ fontSize: '0.78rem', marginTop: '0.5rem' }}>
          A tenant is the isolation boundary. Sharing a corpus grants read access only — writes are always owner-only.
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
          <p style={{ marginTop: 0, fontSize: '0.875rem', color: 'var(--warn)' }}>
            <strong>Copy it now.</strong> This is the only time it will be shown — it is stored as a hash and cannot be recovered.
          </p>
          <pre className="mono" style={{ background: 'var(--surface-2)', padding: '0.7rem', borderRadius: 7, fontSize: '0.78rem', overflowX: 'auto', whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>
            {issued.secret}
          </pre>
          <div style={{ display: 'flex', gap: '0.5rem', marginBottom: '1rem' }}>
            <CopyButton text={issued.secret} label="Copy token" />
          </div>

          {/* The step between "installed" and "working". */}
          <p style={{ fontSize: '0.8rem', fontWeight: 600, margin: '0 0 0.35rem' }}>Connect an agent</p>
          <pre className="mono" style={{ background: 'var(--surface-2)', padding: '0.7rem', borderRadius: 7, fontSize: '0.75rem', overflowX: 'auto', whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>
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
        <Field label="Name" hint="What this token is for — it appears in the audit log.">
          <input className="input" value={name} onChange={(e) => setName(e.target.value)} placeholder="claude-code" autoFocus />
        </Field>
        <Field label="Scopes" hint="search reads; ingest can trigger reindexing; admin manages tenants, corpora and tokens.">
          <div style={{ display: 'flex', gap: '0.4rem', flexWrap: 'wrap' }}>
            {['search', 'ingest', 'admin'].map((s) => (
              <button key={s} type="button" className="btn" onClick={() => toggle(s)} style={scopes.includes(s) ? { background: 'var(--accent-soft)', color: 'var(--accent)', borderColor: 'color-mix(in oklab, var(--accent) 35%, transparent)' } : undefined}>
                {scopes.includes(s) ? '✓ ' : ''}{s}
              </button>
            ))}
          </div>
        </Field>
        <div style={{ display: 'flex', gap: '0.5rem', justifyContent: 'flex-end', marginTop: '1rem' }}>
          <button type="button" className="btn" onClick={onClose}>Cancel</button>
          <button type="submit" className="btn btn-primary" disabled={!name.trim() || scopes.length === 0 || busy}>
            {busy ? <Spinner /> : null} Create
          </button>
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
  // asks, once, and reports what came back — including how long it took, because a
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
    <div style={{ display: 'grid', gap: '1.2rem', maxWidth: 680 }}>
      <h1 style={{ margin: 0, fontSize: '1.15rem' }}>Settings</h1>

      <section className="card" style={{ padding: '0.9rem' }}>
        <h2 style={{ margin: '0 0 0.7rem', fontSize: '0.95rem' }}>Appearance</h2>
        <div style={{ display: 'flex', gap: '0.4rem' }}>
          {['auto', 'light', 'dark'].map((t) => (
            <button key={t} className="btn" onClick={() => setTheme(t)} style={theme === t ? { background: 'var(--accent-soft)', color: 'var(--accent)' } : undefined}>
              {t}
            </button>
          ))}
        </div>
      </section>

      <section className="card" style={{ padding: '0.9rem' }}>
        <h2 style={{ margin: '0 0 0.7rem', fontSize: '0.95rem' }}>Dependencies</h2>
        {/* Read-only: endpoints come from the environment. Making them editable here
            would mean storing them, and an endpoint is not a user preference. */}
        <div style={{ display: 'grid', gap: '0.45rem', fontSize: '0.85rem' }}>
          <Row label="Qdrant"><span className="mono">{health?.qdrant.endpoint ?? '—'}</span> <Badge tone={health?.qdrant.reachable ? 'ok' : 'danger'}>{health?.qdrant.reachable ? 'reachable' : 'unreachable'}</Badge></Row>
          <Row label="Ollama"><span className="mono">{health?.ollama.endpoint ?? '—'}</span> <Badge tone={health?.ollama.reachable ? 'ok' : 'danger'}>{health?.ollama.reachable ? 'reachable' : 'unreachable'}</Badge></Row>
          <Row label="Model"><span className="mono">{health?.ollama.model ?? '—'}</span> · {health?.ollama.dimensions ?? 0}d</Row>
          <Row label="Corpora">{health?.corpora ?? 0}</Row>
        </div>

        <div style={{ marginTop: '0.8rem' }}>
          <button className="btn" disabled={checking} onClick={() => void checkConnectivity()}>
            {checking ? <Spinner /> : 'Check connectivity'}
          </button>

          <ErrorBanner error={checkError} onDismiss={() => setCheckError(null)} />

          {checked && (
            <div style={{ marginTop: '0.6rem', fontSize: '0.83rem', display: 'grid', gap: '0.3rem' }}>
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
                  <span className="dim" style={{ fontSize: '0.78rem' }}>{checked.health.ollama.error}</span>
                )}
              </Row>
              {!checked.health.ollama.reachable && (
                <p className="dim" style={{ margin: 0, fontSize: '0.78rem' }}>
                  Search still works in <strong>keyword</strong> mode without embeddings, and says so in the
                  response. Indexing will retry and report the files it could not embed.
                </p>
              )}
            </div>
          )}
        </div>
        <p className="dim" style={{ fontSize: '0.78rem', margin: '0.7rem 0 0' }}>
          These come from the environment (DEXICON__QDRANT__ENDPOINT, DEXICON__OLLAMA__ENDPOINT) and are shown read-only.
        </p>
      </section>
    </div>
  );
}
