import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  api,
  ApiError,
  getToken,
  isRunning,
  progressOf,
  setToken,
  subscribeToProgress,
  type Corpus,
  type CoverageGap,
  type Health,
  type IndexedFile,
  type EmbeddingModelInfo,
  type IndexedFileText,
  type Job,
  type Progress,
  type SearchResult,
  type TokenSummary,
} from './api';
import {
  Badge, Button, CardButton, Checkbox, Chip, CopyButton, Empty, ErrorBanner, Field, Input, Modal,
  Notice, Segmented, Select, SelectItem, Spinner, formatBytes, localTime, relativeTime, stateTone,
} from './ui';
import {
  ArrowLeft, Check, Database, FileText, Key, ListChecks, LogOut, Plus, RefreshCw, RotateCcw,
  Search, Settings, Sliders, SlidersHorizontal, Trash2, TriangleAlert,
} from 'lucide-react';
import { cn } from 'cn';
import { unitFor, unitOf } from './lib/units';
import { parseHash, toHash, type View as RouteView } from './route';
import { WorkspacePicker } from './WorkspacePicker';

/** `nomic-embed-text` and `nomic-embed-text:latest` are the same model. */
const sameModelName = (a: string, b: string) =>
  a.replace(/:latest$/i, '').toLowerCase() === b.replace(/:latest$/i, '').toLowerCase();

import { DocumentsView } from './Documents';
import { ChunkSetsPanel, ModelsView } from './ChunkSets';

// The list and the parsing live in route.ts, so the URL and the switch below cannot
// drift apart.
type View = RouteView;

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
  if (!token) return <SignInGate onToken={(t) => { setToken(t); setTok(t); }} />;
  return <Shell onSignOut={() => { void api.signOut(); setToken(null); setTok(null); }} />;
}

/**
 * The password, exchanged for a session bearer.
 *
 * It used to take a pasted API token, which meant the first thing a new install asked of
 * someone was to grep a 60-character secret out of a container log. The key model stayed;
 * it is now for agents, and administration is this.
 */
function SignInGate({ onToken }: { onToken: (t: string) => void }) {
  const [value, setValue] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      // Anonymous, so the stale bearer of a previous session must not be sent with it.
      setToken(null);
      const session = await api.signIn(value);
      setToken(session.token);
      onToken(session.token);
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
          Sign in to continue.
        </p>

        <Field
          label="Admin password"
          hint="On a fresh install it is printed once in the container log: docker compose logs dexicon | grep 'admin password'. Set DEXICON_ADMIN_PASSWORD in .env to pin your own."
        >
          <Input
            type="password"
            autoComplete="current-password"
            autoFocus
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
  // Seeded from the URL, so a reload, a bookmark and a shared link all open the screen they
  // name. Before this, every reload landed on Search whatever you were reading.
  const initial = parseHash(window.location.hash);
  const [view, setView] = useState<View>(initial.view);
  const [health, setHealth] = useState<Health | null>(null);
  const [corpora, setCorpora] = useState<Corpus[]>([]);
  const [live, setLive] = useState<Record<string, Progress>>({});
  const [connected, setConnected] = useState(true);
  const [healthStale, setHealthStale] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [selected, setSelected] = useState<string | null>(initial.corpus ?? null);

  // State to URL. Assigning only when it differs is what stops this and the listener below
  // from handing the same value back and forth; assigning at all is what puts an entry in
  // history, which is what makes the back button work.
  useEffect(() => {
    const want = toHash({ view, corpus: selected ?? undefined });
    if (window.location.hash !== want) window.location.hash = want;
  }, [view, selected]);

  // URL to state, for the back and forward buttons and for a hand-edited address.
  useEffect(() => {
    const onHashChange = () => {
      const route = parseHash(window.location.hash);
      setView(route.view);
      setSelected(route.corpus ?? null);
    };

    window.addEventListener('hashchange', onHashChange);
    return () => window.removeEventListener('hashchange', onHashChange);
  }, []);

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

  // Seed from the jobs listing, because the stream only carries what happens NEXT.
  // Opening the page during an index showed the corpus badge saying "indexing" with no
  // progress under it until the next event arrived, which during extraction of a large
  // PDF is tens of seconds of a page that looks stuck.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const jobs = await api.listJobs();
        if (cancelled) return;
        const running = jobs.filter((j) => j.state === 'running' || j.state === 'queued');
        if (running.length === 0) return;
        // Mapped into the shape the stream sends, so `live` holds one kind of thing.
        // Seeded as a JobSummary and updated as an IndexProgress, every reader had to
        // guess which it had, and the guesses were wrong in both directions.
        setLive((prev) => {
          const next = { ...prev };
          // Only where the stream has said nothing yet: an event is fresher than a poll.
          for (const j of running) next[j.corpusId] ??= progressOf(j);
          return next;
        });
      } catch {
        // The bar is a convenience. Failing to seed it is not worth an error banner.
      }
    })();
    return () => { cancelled = true; };
  }, []);

  useEffect(
    () =>
      subscribeToProgress(
        (p) => {
          setLive((prev) => ({ ...prev, [p.corpusId]: p }));
          // A job reaching a terminal state changes the corpus counts too. This asked
          // for `p.state`, which the event has never carried, so the guard was false on
          // every event and the counts only moved on the next poll.
          if (!isRunning(p)) void refreshCorpora();
        },
        () => setConnected(false),
        () => setConnected(true),
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

export function HealthDots({ health, connected, stale }: { health: Health | null; connected: boolean; stale: boolean }) {
  const [open, setOpen] = useState(false);
  const wrapper = useRef<HTMLDivElement>(null);

  // Escape and a click elsewhere, which is what every other popover on the web does and
  // what the rest of this app's dialogs already do. Without them the panel could only be
  // dismissed by finding the button again, so it sat over the page while you worked.
  useEffect(() => {
    if (!open) return;

    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false);
    const onPointer = (e: PointerEvent) => {
      if (!wrapper.current?.contains(e.target as Node)) setOpen(false);
    };

    document.addEventListener('keydown', onKey);
    document.addEventListener('pointerdown', onPointer);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('pointerdown', onPointer);
    };
  }, [open]);

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

  const missing = health?.missingModels ?? [];

  return (
    <div className="relative" ref={wrapper}>
      <Button
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        title={missing.length > 0
          ? `${missing.length} embedding model${missing.length === 1 ? '' : 's'} a chunk set needs ` +
            `${missing.length === 1 ? 'is' : 'are'} missing`
          : 'Dependency health'}
      >
        <span style={dot(health?.qdrant.reachable)} /> qdrant
        {/* The configured provider names itself. Hardcoding "ollama" labelled the dot
            with the wrong backend on any deployment whose default is not Ollama. */}
        <span className="ml-1.5" style={dot(health?.ollama.reachable)} /> {health?.ollama.provider ?? 'embeddings'}
        {/* Both dots can be green while a set is unsearchable, so the fault needs a mark
            of its own out here. Behind the click it is a fault the screen knows about and
            does not mention. */}
        {missing.length > 0 && (
          <>
            <TriangleAlert aria-hidden className="ml-1 size-3.5 text-[var(--warn-text)]" />
            <span className="sr-only">
              {missing.length} embedding model{missing.length === 1 ? '' : 's'} missing
            </span>
          </>
        )}
        {stale && <span className="dim ml-1.5 text-xs">· checking</span>}
        {!connected && <span className="dim ml-1.5 text-xs">· reconnecting</span>}
      </Button>

      {/* The same entrance the dialogs use. This is the one panel in the app that is
          hand-rolled rather than a shadcn primitive, and it was the only one that appeared
          with a hard cut. `origin-top-right` so it grows from the button it belongs to
          rather than from its own middle. Reduced motion collapses the duration, handled
          once in index.css. */}
      {open && health && (
        <div className="card absolute top-10 right-0 z-20 w-[330px] origin-top-right p-3 text-xs shadow-lg animate-in fade-in-0 zoom-in-95 duration-150">
          <p className="mt-0 mx-0 mb-2 font-semibold">Qdrant</p>
          <p className="dim mono m-0 break-all">{health.qdrant.endpoint}</p>
          <p className="mt-1 mx-0 mb-3">
            <Badge tone={health.qdrant.reachable ? 'ok' : 'danger'}>{health.qdrant.reachable ? 'reachable' : 'unreachable'}</Badge>
          </p>

          {/* The provider's own name, not "Ollama": the default provider is configurable
              and the response has carried `provider` all along. */}
          <p className="mt-0 mx-0 mb-2 font-semibold">
            Embeddings <span className="dim font-normal">· {health.ollama.provider}</span>
          </p>
          <p className="dim mono m-0 break-all">{health.ollama.endpoint}</p>
          {/* Reachability, said the same way Qdrant says it. This badge used to carry the
              model name instead, so the panel never plainly answered the one question it
              exists for, and colour-coded a model name by whether the backend was up. */}
          <p className="mt-1 mx-0 mb-0">
            <Badge tone={health.ollama.reachable ? 'ok' : 'danger'}>
              {health.ollama.reachable ? 'reachable' : 'unreachable'}
            </Badge>
          </p>
          {/* The default model and its dimensionality used to sit here as two badges,
              which read as "this is what Dexicon embeds with" — true of nothing, since
              every set pins its own. It is also already on screen at the one moment it
              applies, in the corpus form. What belongs in a health panel is the inverse:
              a set whose model the provider no longer has. That set still says `ready`
              and its vectors are still in Qdrant, but its queries cannot be embedded.

              `missing`, not `health.missingModels`: the field is newer than the container
              a dev loop may be running against, and reading `.length` off the undefined it
              returns took the whole page down the moment the panel was opened. See the
              same note on a source's globs below. */}
          {missing.length > 0 && (
            <div className="mt-2.5 grid gap-1.5">
              {missing.map((m) => (
                <Notice key={`${m.provider}/${m.model}`} tone="danger" className="text-xs">
                  <strong className="mono">{m.model}</strong> is gone from{' '}
                  <span className="mono">{m.provider}</span>, and{' '}
                  {m.sets.length === 1 ? 'a chunk set needs it' : `${m.sets.length} chunk sets need it`}:{' '}
                  <span className="mono">{m.sets.join(', ')}</span>. Searching{' '}
                  {m.sets.length === 1 ? 'it' : 'them'} returns nothing until the model is
                  pulled back.
                </Notice>
              ))}
            </div>
          )}
          {health.ollama.error && (
            <p className="mt-2 mx-0 mb-0 text-[var(--danger-text)]">{health.ollama.error}</p>
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

// ── Snippets ────────────────────────────────────────────────────────────────

/**
 * Languages whose chunks are code, and are therefore worth the monospace.
 *
 * Everything else is prose, which monospace actively harms: this corpus is ~15,000 chunks
 * of book, and a page of Designing Data-Intensive Applications set in 12px monospace is
 * slower to read than the same page anywhere else. A path or an identifier keeps it,
 * because column alignment and character distinction are the point there.
 */
const CODE_LANGUAGES = new Set([
  'csharp', 'typescript', 'javascript', 'tsx', 'jsx', 'python', 'go', 'rust', 'java',
  'cpp', 'c', 'sql', 'json', 'yaml', 'yml', 'xml', 'html', 'css', 'scss', 'shell',
  'bash', 'powershell', 'ruby', 'php', 'kotlin', 'swift', 'scala', 'toml', 'ini',
  'dockerfile', 'makefile', 'diff',
]);

function isProse(language?: string | null): boolean {
  return !language || !CODE_LANGUAGES.has(language.toLowerCase());
}

/**
 * Whether a file's NAME is a title rather than a path.
 *
 * By extension, because this decides how to set the name itself and the name is all there
 * is at that point. "A Project Guide to UX Design - For User Experience Designers in the
 * Field or in the Making, 3rd Edition.epub" is a sentence; in 12px monospace with
 * break-all it wrapped as "3rd Editio / n.epub".
 */
const DOCUMENT_EXTENSIONS = /\.(pdf|epub|docx?|pptx?|md|markdown|txt|rtf)$/i;

function isDocumentName(path: string): boolean {
  return DOCUMENT_EXTENSIONS.test(path) && !path.includes('/');
}

const REGEX_SPECIALS = /[.*+?^${}()|[\]\\]/g;

/**
 * Words common enough that marking them marks the passage.
 *
 * "chunking strategy and overlap size" over the books corpus produced 133 marks, 47 of
 * them the word "and". A three-character floor does not separate these on its own: "API",
 * "RRF" and "PDF" all clear it and all carry the query.
 */
const STOPWORDS = new Set([
  'and', 'the', 'for', 'are', 'but', 'not', 'was', 'were', 'you', 'your', 'all', 'any',
  'can', 'has', 'had', 'its', 'our', 'out', 'own', 'how', 'why', 'who',
  'that', 'this', 'with', 'from', 'they', 'them', 'their', 'there', 'then', 'than',
  'have', 'what', 'when', 'where', 'which', 'while', 'will', 'would', 'should',
  'about', 'into', 'over', 'some', 'such', 'only', 'other', 'been', 'being',
  'does', 'did', 'each', 'more', 'most', 'much', 'very', 'just', 'also', 'here',
]);

/**
 * Split text into alternating non-match / match segments for the query's terms.
 *
 * Terms of three characters or more, minus the stopwords. `split` with ONE capture group
 * returns matches at the odd indices, which is what the caller relies on.
 */
function splitOnTerms(text: string, query: string): string[] {
  const terms = [...new Set((query.toLowerCase().match(/[\p{L}\p{N}_]{3,}/gu) ?? []))]
    .filter((t) => !STOPWORDS.has(t));
  if (terms.length === 0) return [text];

  const pattern = terms
    .sort((a, b) => b.length - a.length)   // longest first, so "corpus_id" wins over "corpus"
    .map((t) => t.replace(REGEX_SPECIALS, '\\$&'))
    .join('|');

  try {
    return text.split(new RegExp(`(${pattern})`, 'giu'));
  } catch {
    // A term that survives escaping and still will not compile must not lose the snippet.
    return [text];
  }
}

/**
 * A search hit's passage.
 *
 * Marks the query's terms, because the alternative is handing someone 1,500 characters and
 * letting them find the reason themselves. The mark is amber rather than the accent, which
 * already means "default" and "in use" elsewhere in this UI.
 */
function Snippet({ content, language, query }: { content: string; language?: string | null; query: string }) {
  const prose = isProse(language);
  const parts = splitOnTerms(content, query);

  return (
    <pre
      className={cn(
        'm-0 max-h-[340px] overflow-x-auto rounded-md bg-muted px-3 py-2.5 break-words whitespace-pre-wrap',
        // font-sans explicitly: <pre> is monospace in the UA stylesheet, so setting only
        // the size and the leading left the book pages in 14px monospace.
        prose ? 'font-sans text-sm leading-relaxed' : 'mono text-xs',
      )}
    >
      {parts.map((part, i) =>
        i % 2 === 1
          ? <mark key={i} className="rounded-[2px] bg-[var(--mark-bg)] px-[0.1em] text-[var(--mark-text)]">{part}</mark>
          : part,
      )}
    </pre>
  );
}

/** Shown while a search runs. See the comment at its use. */
function SearchSkeleton({ count = 3 }: { count?: number }) {
  return (
    <div className="grid gap-3" aria-hidden>
      {Array.from({ length: count }, (_, i) => (
        <div key={i} className="card p-3.5">
          <div className="mb-2 h-4 w-1/2 animate-pulse rounded bg-muted" />
          <div className="grid gap-1.5 rounded-md bg-muted px-3 py-2.5">
            <div className="h-3 w-full animate-pulse rounded bg-[var(--border)]" />
            <div className="h-3 w-[92%] animate-pulse rounded bg-[var(--border)]" />
            <div className="h-3 w-[78%] animate-pulse rounded bg-[var(--border)]" />
          </div>
        </div>
      ))}
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
      {/* Every other view names itself in an h1; this was the one screen with no heading
          at all, so the headings outline skipped it and a screen reader arrived somewhere
          unnamed. Not shown, because a "Search" title sitting on top of a search box that
          already says what it is would be furniture. */}
      <h1 className="sr-only">Search</h1>

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
            <SelectItem value={ALL_CORPORA}>All corpora</SelectItem>
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
        <Notice tone="warn">
          <strong>Degraded:</strong> {result.degradedReason}
        </Notice>
      )}
      {/* An incomplete result is a warning, not a footnote. This was drawn in the accent
          blue, which is the colour the app uses for "default" and "in use", so the one
          line saying the index was still building looked like a label on it. */}
      {result?.note && <Notice tone="warn">{result.note}</Notice>}

      {/* A search over 15,000 chunks measured 5.4 s on this machine, and the only sign it
          was running was a spinner inside the button. At that length a spinner reads as a
          hung page; cards in the shape of the answer read as work in progress. The previous
          result is hidden rather than left underneath, because a header reading "10 results
          · 5028 ms" over passages from the last query is a stale answer to the new one. */}
      {busy && <SearchSkeleton />}

      {result && !busy && (
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
                // `min-w-0`: a grid item's default `min-width: auto` will not let the
                // track be narrower than the item's own min-content, and this header's
                // min-content is a whole book title plus an unshrinkable section label.
                // The citation did truncate — after widening the track past the viewport,
                // which put a horizontal scrollbar under the entire page and pushed the
                // search box and the results count off the right-hand edge with it.
                <article key={`${h.corpusId}-${h.filePath}-${h.startLine}-${i}`} className="card min-w-0 p-3.5">
                  {/* The citation truncates; the actions do not move.
                      A book's filename is long — "Coaching Agile Teams - A Companion for
                      ScrumMasters, Agile Coaches, and Project Managers in Transition.epub"
                      — and wrapping it pushed Copy path and Open onto a second line, so
                      the controls sat in a different place on every result. The full text
                      is still on the element and in Copy path, which is how anyone
                      actually takes a citation. */}
                  {/* `flex-wrap` is inert where there is room, so the row still reads as
                      one line on a desktop. At phone width there is not room, and without
                      it Copy path and Open sat 80px past the right edge of the page. */}
                  <header className="flex flex-wrap gap-2.5 items-center mb-2">
                    {/* The fragment and the section label are the same fact. `book.pdf#page=198`
                        beside a `Page 198` badge spent two slots of one glance on one number, so
                        the fragment is dropped from the DISPLAY when a section is shown. The full
                        citation stays on the title attribute and in Copy path, which is what
                        anyone actually takes away. */}
                    <code
                      className="mono truncate text-sm font-semibold"
                      title={h.location ?? undefined}
                    >
                      {h.section ? (h.location ?? '').split('#')[0] : h.location}
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
                  <Snippet content={h.content} language={h.language} query={result.query} />
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
  live: Record<string, Progress>;
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
            const running = isRunning(job);
            return (
              <CardButton key={c.id} onClick={() => onOpen(c.name)}>
                <div className="flex gap-2 items-center flex-wrap">
                  <strong>{c.name}</strong>
                  <Badge tone={stateTone(c.state)}>{c.state}</Badge>
                  <span className="flex-1" />
                  <span className="dim text-xs" title={localTime(c.lastIndexedUtc)}>indexed {relativeTime(c.lastIndexedUtc)}</span>
                </div>

                {c.description && <p className="dim mt-1.5 mx-0 mb-0 text-sm">{c.description}</p>}

                <div className="dim mt-2 text-xs flex gap-3.5 flex-wrap">
                  <span>{c.fileCount.toLocaleString()} {unitFor(c.sources, c.fileCount)}</span>
                  <span>{c.chunkCount.toLocaleString()} chunks</span>
                  {/* Discovered by a sweep and not yet indexed. Separate from the file count,
                      which is what is searchable: a corpus added while another indexes used to
                      read as empty until its turn came round. */}
                  {c.pendingCount > 0 && (
                    <span title="found by a discovery sweep, not yet indexed">
                      {c.pendingCount.toLocaleString()} awaiting indexing
                    </span>
                  )}
                  {c.skippedCount > 0 && <span>{c.skippedCount.toLocaleString()} skipped</span>}
                  {c.failedCount > 0 && <span className="text-[var(--danger-text)]">{c.failedCount.toLocaleString()} failed</span>}
                  {/* The default set is what this corpus answers to unqualified. */}
                  <span className="mono">
                    {c.chunkSets.find((s) => s.isDefault)?.embeddingModel ?? c.chunkSets[0]?.embeddingModel ?? '—'}
                  </span>
                  {c.chunkSets.length > 1 && <span>{c.chunkSets.length} chunk sets</span>}
                </div>

                {running && <ProgressBar job={job} sources={c.sources} />}
              </CardButton>
            );
          })}
        </div>
      )}

      {creating && <CreateCorpusModal onClose={() => setCreating(false)} onCreated={async () => { setCreating(false); await onRefresh(); }} onError={onError} />}
    </div>
  );
}

/**
 * @param sources The corpus's sources, so the caption counts what the job is counting.
 * A history job reading "0/201 files" beside a corpus that says "201 commits" is two
 * numbers about the same work disagreeing on what the work is.
 */
function ProgressBar({ job, sources }: {
  job: Progress;
  sources?: Corpus['sources'];
}) {
  const processed = job.filesDone + job.filesSkipped + job.filesFailed;
  const pct = job.filesTotal > 0 ? Math.min(100, (processed / job.filesTotal) * 100) : 0;
  const caption =
    `${job.phase} · ${processed.toLocaleString()}/${job.filesTotal.toLocaleString()}` +
    ` ${unitFor(sources, job.filesTotal)} · ${job.chunksWritten.toLocaleString()} chunks`;

  return (
    <div className="mt-2.5">
      {/* A bare pair of divs is a picture of a progress bar, not a progress bar: nothing
          announced it, so the only indication that an index was running was a coloured
          rectangle. The caption below carries the numbers, so it is the accessible name
          rather than a second thing to keep in step. */}
      <div
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(pct)}
        aria-valuetext={caption}
        className="h-[5px] overflow-hidden rounded-full bg-muted"
      >
        <div
          className="h-full bg-[var(--accent)] transition-[width] duration-300"
          // Live percentage: the one thing that cannot be a class without generating a
          // class per percent.
          style={{ width: `${pct}%` }}
        />
      </div>
      <div className="dim text-xs mt-1">
        {caption}
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

/**
 * Rows per page. What is fetched IS what is rendered: the two were different numbers
 * before, and neither was the one that actually truncated the list.
 */
const FilePageSize = 100;

const CHUNK_SETS_OPEN = 'dexicon.chunkSets.open';

export function CorpusDetail({
  name,
  live,
  onBack,
  onRefresh,
  onError,
}: {
  name: string;
  live: Record<string, Progress>;
  onBack: () => void;
  onRefresh: () => Promise<void>;
  onError: (e: unknown) => void;
}) {
  const [corpus, setCorpus] = useState<Corpus | null>(null);
  const [files, setFiles] = useState<IndexedFile[]>([]);
  // What the query matched, which is what the paging arithmetic is over. Not the
  // corpus's file count: with a name filter those are different numbers.
  const [totalFiles, setTotalFiles] = useState(0);
  const [offset, setOffset] = useState(0);
  const [sort, setSort] = useState('path');

  // Debounced, because this now costs a round trip per change rather than a filter over
  // an array. 250ms is below the point a keystroke feels unacknowledged.
  const [nameQuery, setNameQuery] = useState('');
  const [filter, setFilter] = useState<string>('');
  const [nameFilter, setNameFilter] = useState('');
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [confirmFullReindex, setConfirmFullReindex] = useState(false);

  // Collapsed unless someone opened it last time, remembered per browser. The line it
  // collapses to already says which model search uses, which is what most visits want.
  const [chunkSetsOpen, setChunkSetsOpen] = useState(() => {
    try { return localStorage.getItem(CHUNK_SETS_OPEN) === '1'; } catch { return false; }
  });
  const chunkSets = useRef<HTMLDivElement>(null);
  const leavingForChunkSets = useRef(false);
  const showChunkSets = (open: boolean) => {
    setChunkSetsOpen(open);
    try { localStorage.setItem(CHUNK_SETS_OPEN, open ? '1' : '0'); } catch { /* blocked storage is fine */ }
  };
  const [removingSource, setRemovingSource] = useState<Corpus['sources'][number] | null>(null);
  const [addingSource, setAddingSource] = useState<{ path: string } | null>(null);
  const [viewing, setViewing] = useState<{ path: string; line?: number } | null>(null);
  const [gaps, setGaps] = useState<CoverageGap[]>([]);
  const [editingSource, setEditingSource] = useState<Corpus['sources'][number] | null>(null);
  const [editingDefaults, setEditingDefaults] = useState(false);


  const load = useCallback(async () => {
    try {
      const c = await api.getCorpus(name);
      setCorpus(c);
      const f = await api.listFiles(name, {
        status: filter || undefined,
        name: nameQuery || undefined,
        sort,
        limit: FilePageSize,
        offset,
      });
      setFiles(f.files);
      setTotalFiles(f.total);

      // Its own call, and a failure here does not reach onError: a coverage report that
      // cannot be fetched is a missing warning, not a broken page, and an older container
      // in the dev loop has no such endpoint at all. The page is worth more than the
      // notice.
      try {
        setGaps((await api.coverage(name)).gaps ?? []);
      } catch {
        setGaps([]);
      }
    } catch (e) {
      onError(e);
    }
  }, [name, filter, nameQuery, sort, offset, onError]);

  useEffect(() => { void load(); }, [load]);

  // The typed value becomes the query after a pause. Every change that alters WHICH
  // rows match also returns to the first page: paging arithmetic over a different
  // result set lands somewhere arbitrary, and an empty page reads as no matches.
  //
  // The timer is only armed when the typed value differs from the one in force, so a
  // page the reader turned within the pause is not dragged back to the first. It used to
  // arm on mount and on any keystroke that left the query where it was, and 250ms later
  // reset the offset whether or not anything had changed: clicking Next in that window
  // put the reader back on page one, and the page did not say why.
  useEffect(() => {
    const next = nameFilter.trim();
    if (next === nameQuery) return;

    const t = setTimeout(() => {
      setNameQuery(next);
      setOffset(0);
    }, 250);
    return () => clearTimeout(t);
  }, [nameFilter, nameQuery]);

  useEffect(() => { setOffset(0); }, [filter, sort]);

  const job = corpus ? live[corpus.id] : undefined;

  // Both, because they answer at different speeds. The live job knows within a second of
  // a refresh starting; `corpus.state` survives a page load, when nothing is streaming
  // yet and the counts on screen are still a partial tally from a run already underway.
  const indexing = isRunning(job) || corpus?.state === 'indexing';

  // Reload when the run ENDS, once. This waited for `phase` to go absent, and it never
  // does: the last event of a run carries the state name in that field, so the counts
  // sat at whatever they were before the run for as long as the page was open.
  //
  // Keyed on the job rather than fired on the condition, because the finished event then
  // STAYS in `live` — nothing clears it — and `load` changes identity whenever the
  // filter, the sort or the page does. Without the key, every one of those interactions
  // fetched the corpus twice for the rest of the session.
  const reloadedFor = useRef<string | null>(null);
  useEffect(() => {
    if (!job || isRunning(job) || reloadedFor.current === job.jobId) return;
    reloadedFor.current = job.jobId;
    void load();
  }, [job, load]);

  if (!corpus) return <Empty title="Loading…" />;

  const problems = files.filter((f) => f.status !== 'indexed');

  // The server decided both which rows match and their order, so this is the page and
  // nothing filters or slices it again. Doing either here is what limited a search to
  // the rows that happened to have been fetched.
  const shown = files;

  const pageFrom = totalFiles === 0 ? 0 : offset + 1;
  const pageTo = offset + files.length;
  const morePages = pageTo < totalFiles;
  const settling = nameFilter.trim() !== nameQuery;

  return (
    <div className="grid gap-4">
      <div className="flex gap-2.5 items-center flex-wrap">
        <Button onClick={onBack}><ArrowLeft />Corpora</Button>
        <h1 className="m-0 text-lg">{corpus.name}</h1>
        <Badge tone={stateTone(corpus.state)}>{corpus.state}</Badge>
        <span className="flex-1" />
        <Button onClick={async () => { try { await api.reindex(corpus.name); } catch (e) { onError(e); } }}><RefreshCw />Refresh</Button>
        {/* Asks first. Refresh is cheap and idempotent; this one re-embeds a corpus that
            may have taken hours, and it sat one click away from it with nothing between. */}
        <Button onClick={() => { leavingForChunkSets.current = false; setConfirmFullReindex(true); }}><RotateCcw />Full reindex</Button>
        <Button variant="danger" onClick={() => setConfirmDelete(true)}><Trash2 />Delete</Button>
      </div>

      {job?.phase && <div className="card p-3.5"><ProgressBar job={job} sources={corpus.sources} /></div>}

      <CoverageNotice
        gaps={gaps}
        canAddSource
        onAddSource={(path) => setAddingSource({ path })}
      />

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
                      is invisible in a corpus-level count.

                      While the walk is running the count is a partial tally, so it must
                      not be read as a finding. A source added a moment ago sat at "no
                      files" in the warning colour, next to a corpus badge reading
                      "indexing", and there was no way to tell that from a mistyped path.
                      It says what it is instead, and stops claiming anything until the
                      run that would justify the claim has finished. */}
                  {corpus.sources.length > 1 && (
                    indexing ? (
                      <span className="dim text-xs">
                        {s.fileCount
                          ? `${s.fileCount.toLocaleString()} ${unitOf(s, s.fileCount)} so far`
                          : 'counting…'}
                      </span>
                    ) : (
                      <span className={s.fileCount ? 'dim text-xs' : 'text-xs text-[var(--warn-text)]'}>
                        {s.fileCount
                          ? `${s.fileCount.toLocaleString()} ${unitOf(s, s.fileCount)}`
                          : `no ${unitOf(s, 0)}`}
                      </span>
                    )
                  )}
                  {/* A history source ignores the size cap and .gitignore, and what it
                      counts is commits. Rendering the file settings against one said it
                      obeyed three things it does not read, and called its commits files. */}
                  {s.kind === 'githistory' ? (
                    <span className="dim text-xs">
                      {s.git?.ref ?? 'HEAD'}
                      {' · '}{s.git?.includeDiff ? 'with the diff' : 'message and stat'}
                      {s.includeGlobs?.length ? ` · only ${s.includeGlobs.join(', ')}` : ''}
                    </span>
                  ) : (
                    <span className="dim text-xs">
                      {s.useGitignore ? '.gitignore honoured' : '.gitignore ignored'}
                      {' · '}≤ {formatBytes(s.maxFileBytes)}
                      {s.includeGlobs?.length ? ` · only ${s.includeGlobs.join(', ')}` : ''}
                      {s.excludeGlobs?.length ? ` · not ${s.excludeGlobs.join(', ')}` : ''}
                      {/* Which of those the source would keep if the corpus default moved.
                          Without it a reader reads every value as one they typed here, and
                          editing the corpus default looks like it did nothing. */}
                      {inheritsFromCorpus(s, corpus.defaults) && ' · some from the corpus'}
                    </span>
                  )}
                  {/* Adding a folder was one click; removing one meant deleting the whole
                      corpus and rebuilding it, losing its chunk sets, its history and every
                      other source with it. A path typed wrong is not worth that. */}
                  {s.kind === 'workspace' && (
                    <Button
                      variant="ghost"
                      size="icon-xs"
                      aria-label={`Edit filters for ${s.rootPath ?? s.kind}`}
                      onClick={() => setEditingSource(s)}
                    >
                      <SlidersHorizontal />
                    </Button>
                  )}
                  <Button
                    variant="ghost"
                    size="icon-xs"
                    aria-label={`Remove source ${s.rootPath ?? s.kind}`}
                    onClick={() => setRemovingSource(s)}
                  >
                    <Trash2 />
                  </Button>
                </span>
              ))}
            </span>
          )}
          {(
            <span className="mt-1.5 flex flex-wrap gap-2">
              <Button className="px-2 py-0.5 text-xs" onClick={() => setAddingSource({ path: '' })}>
                <Plus />
                Add source
              </Button>
              {/* Set once here rather than repeated on every folder. Ten sources under one
                  parent used to carry ten copies of the same two globs. */}
              <Button className="px-2 py-0.5 text-xs" onClick={() => setEditingDefaults(true)}>
                <SlidersHorizontal />
                Default filters
              </Button>
            </span>
          )}
        </Row>
        <Row label="Searched as">
          <span className="mono">{corpus.name}</span>
          <span className="dim">
            {' '}is the default set below. Name another with <span className="mono">corpus:set</span>.
          </span>
        </Row>
        <Row label="Contents">
          {corpus.fileCount.toLocaleString()} {unitFor(corpus.sources, corpus.fileCount)}
          {' · '}{corpus.chunkCount.toLocaleString()} chunks
          {corpus.skippedCount > 0 && ` · ${corpus.skippedCount} skipped`}
          {corpus.failedCount > 0 && ` · ${corpus.failedCount} failed`}
        </Row>
      </div>

      <div ref={chunkSets} className="card p-3.5 scroll-mt-4">
        <ChunkSetsPanel
          corpus={corpus}
          onChanged={async () => { await load(); await onRefresh(); }}
          open={chunkSetsOpen}
          onOpenChange={showChunkSets}
        />
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
          {/* Ninety-six books, alphabetical, each present twice as PDF and EPUB. Narrowing
              by status does not help when you know the title and want that one row. */}
          <Input
            type="search"
            value={nameFilter}
            onChange={(e) => setNameFilter(e.target.value)}
            placeholder="Filter by name…"
            aria-label={`Filter ${unitFor(corpus.sources, 0)} by name`}
            className="h-8 w-[220px] text-sm"
          />
          <Segmented
            label={`Sort ${unitFor(corpus.sources, 0)} by`}
            value={sort}
            onChange={setSort}
            options={[
              { value: 'path', label: 'name' },
              { value: 'size', label: 'size' },
              { value: 'chunks', label: 'chunks' },
              { value: 'status', label: 'status' },
            ]}
          />
          {problems.length > 0 && <span className="dim text-xs">{problems.length} need attention</span>}
        </div>

        {files.length === 0 && totalFiles === 0 && !nameQuery && !filter ? (
          <Empty
            title={`No ${unitFor(corpus.sources, 0)}`}
            hint="Run a refresh to index this corpus."
          />
        ) : files.length === 0 ? (
          <Empty
            title={`No ${unitFor(corpus.sources, 1)} matches that`}
            hint={`Searched all ${corpus.fileCount.toLocaleString()} ${unitFor(corpus.sources, corpus.fileCount)} in this corpus. Clear the filter to see them.`}
          />
        ) : (
          <div className="card overflow-hidden">
            {shown.map((f, i) => (
              <div
                key={f.id}
                className={cn(
                  'flex flex-wrap items-baseline gap-2.5 px-3 py-2 transition-colors hover:bg-muted',
                  i && 'border-t border-border',
                )}
              >
                {/* A path, not a title. Two sources of one corpus can hold the same file
                    name, so showing only the name is the screen telling you it knows
                    something it will not say. */}
                <button
                  type="button"
                  className={cn(
                    'min-w-[220px] flex-1 text-left underline-offset-2 hover:underline',
                    'focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none',
                    // break-all is right for a path, where any character is a fair place
                    // to wrap, and wrong for a title, where it breaks mid-word.
                    isDocumentName(f.relativePath) ? 'text-sm break-words' : 'mono text-xs break-all',
                  )}
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

        {/* The list showed 100 of 190 and said nothing, and the guard against that could
            not fire: the API paged at 100, the list rendered at most 300 of what it held,
            and the notice wanted more than 300 FETCHED rows, which could not happen. The
            fix is not a better notice but a page you can leave — the server now decides
            which rows match and in what order, so every file is reachable. */}
        {totalFiles > 0 && (
          <div className="dim mt-2 flex items-center gap-2 text-xs">
            <span>
              {pageFrom.toLocaleString()}–{pageTo.toLocaleString()} of{' '}
              {totalFiles.toLocaleString()}
              {(nameQuery || filter) && ' matching'}
              {settling && ' · filtering…'}
            </span>
            <span className="flex-1" />
            <Button
              className="h-7 px-2"
              disabled={offset === 0}
              onClick={() => setOffset(Math.max(0, offset - FilePageSize))}
            >
              Previous
            </Button>
            <Button className="h-7 px-2" disabled={!morePages} onClick={() => setOffset(pageTo)}>
              Next
            </Button>
          </div>
        )}
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
          initialPath={addingSource.path}
          onClose={() => setAddingSource(null)}
          onAdded={async () => { setAddingSource(null); await onRefresh(); await load(); }}
          onError={onError}
        />
      )}

      {editingSource && (
        <EditSourceModal
          corpus={corpus}
          source={editingSource}
          onClose={() => setEditingSource(null)}
          onSaved={async () => { setEditingSource(null); await load(); }}
          onError={onError}
        />
      )}

      {editingDefaults && (
        <CorpusDefaultsModal
          corpus={corpus}
          onClose={() => setEditingDefaults(false)}
          onSaved={async () => { setEditingDefaults(false); await load(); }}
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

      {confirmFullReindex && (
        <FullReindexModal
          corpus={corpus}
          onClose={() => setConfirmFullReindex(false)}
          onError={onError}
          onShowChunkSets={() => {
            leavingForChunkSets.current = true;
            setConfirmFullReindex(false);
            showChunkSets(true);
            requestAnimationFrame(() => chunkSets.current?.scrollIntoView?.({ behavior: 'smooth', block: 'start' }));
          }}
          // Focus follows the page there, rather than returning to Full reindex while the
          // view has moved to the chunk sets.
          finalFocus={() =>
            leavingForChunkSets.current
              ? chunkSets.current?.querySelector<HTMLElement>('button[aria-expanded]') ?? null
              : null}
        />
      )}
    </div>
  );
}

/**
 * Files the corpus is not indexing and has no row for.
 *
 * A file outside every source root is not skipped and not failed: it is absent. It appears
 * in no count on this page, and a search for it returns other documents, which reads like a
 * ranking result. Nothing on the screen said the corpus had a hole in it.
 *
 * Reported only where two or more of the corpus's sources share a parent directory, so the
 * ordinary case stays silent — a corpus that indexes one folder is not missing everything
 * beside it. The server decides that; this draws what it returns.
 *
 * `warn`, not `danger`: nothing is broken, and a file may be sitting there deliberately.
 * The button carries the path so the fix does not depend on the reader retyping it.
 */
function CoverageNotice({
  gaps,
  canAddSource,
  onAddSource,
}: {
  gaps: CoverageGap[];
  canAddSource: boolean;
  onAddSource: (path: string) => void;
}) {
  if (gaps.length === 0) return null;

  return (
    <>
      {gaps.map((gap) => {
        // The empty string is the workspace root, and "files in  are not indexed" is how
        // that reads if it is passed through.
        const where = gap.directory === '' ? 'the workspace root' : gap.directory;
        const files = gap.files ?? [];

        return (
          <Notice key={gap.directory} tone="warn">
            <p className="m-0">
              <strong>{files.length.toLocaleString()}</strong>{' '}
              {files.length === 1 ? 'file' : 'files'} in <span className="mono">{where}</span>{' '}
              {files.length === 1 ? 'is' : 'are'} covered by no source, though its subfolders
              are. Searching will never return {files.length === 1 ? 'it' : 'them'}.
            </p>

            {/* Five, then a count. The whole list belongs behind the Files tab; what this
                has to do is make the gap concrete enough to recognise. */}
            <ul className="mt-1.5 mb-0 grid gap-0.5 pl-4 text-xs">
              {files.slice(0, 5).map((f) => (
                <li key={f} className="mono break-all">{f}</li>
              ))}
              {files.length > 5 && (
                <li className="dim">and {(files.length - 5).toLocaleString()} more</li>
              )}
            </ul>

            {canAddSource && (
              <Button
                className="mt-2.5 px-2 py-0.5 text-xs"
                onClick={() => onAddSource(gap.directory)}
              >
                <Plus />
                Add a source on {where}
              </Button>
            )}
          </Notice>
        );
      })}
    </>
  );
}

/** A comma or newline separated list, with the blanks dropped. */
function globList(raw: string): string[] {
  return raw.split(/[\n,]/).map((g) => g.trim()).filter(Boolean);
}

/**
 * Whether this source takes a filter the CORPUS actually sets.
 *
 * Not "inherits anything": almost every source inherits something, because a source with
 * no globs is inheriting the absence of them, and saying so on every row is noise that
 * teaches a reader to skip the line. It is only worth a word when a corpus-level value is
 * really reaching this source.
 */
function inheritsFromCorpus(
  s: Corpus['sources'][number],
  d: Corpus['defaults'],
): boolean {
  if (!d) return false;
  return (d.useGitignore != null && s.ownUseGitignore == null)
    || (d.maxFileBytes != null && s.ownMaxFileBytes == null)
    || (d.includeGlobs != null && s.ownIncludeGlobs == null)
    || (d.excludeGlobs != null && s.ownExcludeGlobs == null);
}

/**
 * One field that either follows the corpus or does not.
 *
 * The checkbox is the whole of inheritance as a reader meets it, so it says what the
 * inherited value actually is. "Use the corpus default" on its own asks someone to accept
 * a value they cannot see, and the answer to "what will this do" is the only thing the
 * control is for.
 */
function Inheritable({
  label,
  inherited,
  inheritedLabel,
  onInheritedChange,
  children,
}: {
  label: string;
  inherited: boolean;
  inheritedLabel: string;
  onInheritedChange: (v: boolean) => void;
  children: ReactNode;
}) {
  return (
    <div className="mb-3.5 grid gap-1.5">
      <label className="flex items-center gap-2">
        <Checkbox
          checked={inherited}
          onCheckedChange={(v) => onInheritedChange(v === true)}
          aria-label={`${label}: use the corpus default`}
        />
        <span className="text-xs text-muted-foreground">
          Corpus default ({inheritedLabel})
        </span>
      </label>
      {!inherited && children}
    </div>
  );
}

/**
 * Change one source's filters.
 *
 * They were write-once: set when the folder was added and unreachable afterwards, so
 * changing one meant deleting the source — which drops its files from every chunk set —
 * and re-embedding the folder from scratch.
 *
 * Saving queues a refresh only when something moved. Narrowing a filter removes the files
 * it now excludes through the walk's own reconcile: they are simply not seen next pass,
 * which is the path a file deleted from disk already takes.
 */
function EditSourceModal({
  corpus,
  source,
  onClose,
  onSaved,
  onError,
}: {
  corpus: Corpus;
  source: Corpus['sources'][number];
  onClose: () => void;
  onSaved: () => Promise<void>;
  onError: (e: unknown) => void;
}) {
  const d = corpus.defaults;

  // Seeded from what the source itself sets. A null there is the source inheriting, which
  // is what the checkbox starts checked for; the input below it is seeded with the
  // effective value so unchecking does not blank the field.
  const [inheritGitignore, setInheritGitignore] = useState(source.ownUseGitignore == null);
  const [inheritCap, setInheritCap] = useState(source.ownMaxFileBytes == null);
  const [inheritInclude, setInheritInclude] = useState(source.ownIncludeGlobs == null);
  const [inheritExclude, setInheritExclude] = useState(source.ownExcludeGlobs == null);

  const [useGitignore, setUseGitignore] = useState(source.useGitignore);
  const [maxFileMb, setMaxFileMb] = useState(source.maxFileBytes / 1024 / 1024);
  const [include, setInclude] = useState((source.includeGlobs ?? []).join(', '));
  const [exclude, setExclude] = useState((source.excludeGlobs ?? []).join(', '));
  const [busy, setBusy] = useState(false);

  const capMb = maxFileMb > 0;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!capMb) return;
    setBusy(true);
    try {
      // `clear` rather than a null, because the API cannot tell an absent field from an
      // explicit null and would read one as the other.
      const clear: string[] = [];
      if (inheritGitignore) clear.push('useGitignore');
      if (inheritCap) clear.push('maxFileBytes');
      if (inheritInclude) clear.push('includeGlobs');
      if (inheritExclude) clear.push('excludeGlobs');

      await api.updateSource(corpus.name, source.id, {
        clear,
        useGitignore: inheritGitignore ? undefined : useGitignore,
        maxFileBytes: inheritCap ? undefined : Math.round(maxFileMb * 1024 * 1024),
        includeGlobs: inheritInclude ? undefined : globList(include),
        excludeGlobs: inheritExclude ? undefined : globList(exclude),
      });
      await onSaved();
    } catch (err) {
      onError(err);
      setBusy(false);
    }
  }

  return (
    <Modal title={`Filters for ${source.rootPath ?? source.kind}`} onClose={onClose}>
      <form onSubmit={submit}>
        <Inheritable
          label="Honour .gitignore"
          inherited={inheritGitignore}
          inheritedLabel={(d?.useGitignore ?? true) ? '.gitignore honoured' : '.gitignore ignored'}
          onInheritedChange={setInheritGitignore}
        >
          <label className="flex items-start gap-2.5">
            <Checkbox
              checked={useGitignore}
              onCheckedChange={(v) => setUseGitignore(v === true)}
              className="mt-0.5"
            />
            <span className="text-sm leading-none font-semibold">Honour .gitignore</span>
          </label>
        </Inheritable>

        <Inheritable
          label="Largest file"
          inherited={inheritCap}
          inheritedLabel={formatBytes(d?.maxFileBytes ?? source.maxFileBytes)}
          onInheritedChange={setInheritCap}
        >
          <Field label="Largest file (MB)" hint="Anything bigger is skipped and reported, not silently dropped.">
            <Input
              type="number"
              min={0.01}
              step="any"
              value={maxFileMb}
              onChange={(e) => setMaxFileMb(Number(e.target.value))}
            />
          </Field>
          {!capMb && (
            <Notice tone="danger" role="alert" className="text-xs">
              A cap of zero indexes nothing. Tick the box above to follow the corpus instead.
            </Notice>
          )}
        </Inheritable>

        <Inheritable
          label="Only these"
          inherited={inheritInclude}
          inheritedLabel={d?.includeGlobs?.length ? d.includeGlobs.join(', ') : 'everything not excluded'}
          onInheritedChange={setInheritInclude}
        >
          <Field label="Only these" hint="Globs, comma separated. Empty means everything not excluded.">
            <Input className="mono" value={include} onChange={(e) => setInclude(e.target.value)}
              placeholder="src/**, docs/**" />
          </Field>
        </Inheritable>

        <Inheritable
          label="Never these"
          inherited={inheritExclude}
          inheritedLabel={d?.excludeGlobs?.length ? d.excludeGlobs.join(', ') : 'nothing'}
          onInheritedChange={setInheritExclude}
        >
          <Field label="Never these" hint="Globs, comma separated. Applied after the include list.">
            <Input className="mono" value={exclude} onChange={(e) => setExclude(e.target.value)}
              placeholder="**/vendor/**, *.min.js" />
          </Field>
        </Inheritable>

        <Notice tone="neutral" className="text-xs">
          Saving re-walks the corpus. Files a narrower filter now excludes leave the index;
          nothing on disk is touched.
        </Notice>

        <div className="mt-3.5 flex justify-end gap-2">
          <Button type="button" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={busy || !capMb}>
            {busy ? <Spinner /> : <Check />}
            Save filters
          </Button>
        </div>
      </form>
    </Modal>
  );
}

/**
 * Filters every source of this corpus inherits unless it sets its own.
 *
 * Inherited live rather than copied when a source is added: ten folders under one parent
 * is the case this exists for, and a default that only reached the eleventh would leave
 * the other ten to be edited one at a time, which is the problem it is here to solve.
 */
function CorpusDefaultsModal({
  corpus,
  onClose,
  onSaved,
  onError,
}: {
  corpus: Corpus;
  onClose: () => void;
  onSaved: () => Promise<void>;
  onError: (e: unknown) => void;
}) {
  const d = corpus.defaults;

  const [setGitignore, setSetGitignore] = useState(d?.useGitignore != null);
  const [setCap, setSetCap] = useState(d?.maxFileBytes != null);
  const [useGitignore, setUseGitignore] = useState(d?.useGitignore ?? true);
  const [maxFileMb, setMaxFileMb] = useState((d?.maxFileBytes ?? 262_144) / 1024 / 1024);
  const [include, setInclude] = useState((d?.includeGlobs ?? []).join(', '));
  const [exclude, setExclude] = useState((d?.excludeGlobs ?? []).join(', '));
  const [busy, setBusy] = useState(false);

  // A source that sets its own value keeps it. Saying how many, rather than how it works,
  // is what stops this reading as "nothing happened".
  const overriding = corpus.sources.filter((s) =>
    s.ownUseGitignore != null || s.ownMaxFileBytes != null
    || s.ownIncludeGlobs != null || s.ownExcludeGlobs != null).length;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    try {
      await api.updateCorpus(corpus.name, {
        defaults: {
          useGitignore: setGitignore ? useGitignore : null,
          maxFileBytes: setCap ? Math.round(maxFileMb * 1024 * 1024) : null,
          includeGlobs: globList(include).length ? globList(include) : null,
          excludeGlobs: globList(exclude).length ? globList(exclude) : null,
        },
      });
      await onSaved();
    } catch (err) {
      onError(err);
      setBusy(false);
    }
  }

  return (
    <Modal title={`Default filters for ${corpus.name}`} onClose={onClose}>
      <form onSubmit={submit}>
        <p className="mt-0 mb-3.5 text-sm text-muted-foreground">
          Every source here follows these unless it sets its own.
        </p>

        <label className="mb-3.5 flex items-start gap-2.5">
          <Checkbox checked={setGitignore} onCheckedChange={(v) => setSetGitignore(v === true)} className="mt-0.5" />
          <span className="grid gap-0.5">
            <span className="text-sm leading-none font-semibold">Set a .gitignore rule</span>
            <span className="text-xs text-muted-foreground">
              Unticked, sources follow the server's setting.
            </span>
          </span>
        </label>

        {setGitignore && (
          <label className="mb-3.5 ml-6 flex items-start gap-2.5">
            <Checkbox checked={useGitignore} onCheckedChange={(v) => setUseGitignore(v === true)} className="mt-0.5" />
            <span className="text-sm leading-none font-semibold">Honour .gitignore</span>
          </label>
        )}

        <label className="mb-3.5 flex items-start gap-2.5">
          <Checkbox checked={setCap} onCheckedChange={(v) => setSetCap(v === true)} className="mt-0.5" />
          <span className="grid gap-0.5">
            <span className="text-sm leading-none font-semibold">Set a size cap</span>
            <span className="text-xs text-muted-foreground">
              Unticked, sources follow the server's cap.
            </span>
          </span>
        </label>

        {setCap && (
          <div className="ml-6">
            <Field label="Largest file (MB)">
              <Input type="number" min={0.01} step="any" value={maxFileMb}
                onChange={(e) => setMaxFileMb(Number(e.target.value))} />
            </Field>
          </div>
        )}

        <Field label="Only these" hint="Globs, comma separated. Empty means everything not excluded.">
          <Input className="mono" value={include} onChange={(e) => setInclude(e.target.value)}
            placeholder="**/*.cs, **/*.md" />
        </Field>

        <Field label="Never these" hint="Globs, comma separated. Applied after the include list.">
          <Input className="mono" value={exclude} onChange={(e) => setExclude(e.target.value)}
            placeholder="**/*.Designer.cs, **/*.test.ts" />
        </Field>

        {overriding > 0 && (
          <Notice tone="warn" className="text-xs">
            {overriding === 1
              ? '1 source sets some of its own filters and keeps them.'
              : `${overriding} sources set some of their own filters and keep them.`}{' '}
            Open a source to return a field to the default.
          </Notice>
        )}

        <div className="mt-3.5 flex justify-end gap-2">
          <Button type="button" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={busy}>
            {busy ? <Spinner /> : <Check />}
            Save defaults
          </Button>
        </div>
      </form>
    </Modal>
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
  initialPath = '',
  onClose,
  onAdded,
  onError,
}: {
  corpus: Corpus;
  /** Pre-filled when the coverage notice opened this, so the fix is one click from the
   *  warning rather than a path the reader has to retype. */
  initialPath?: string;
  onClose: () => void;
  onAdded: () => Promise<void>;
  onError: (e: unknown) => void;
}) {
  const [path, setPath] = useState(initialPath);
  const [useGitignore, setUseGitignore] = useState(true);
  // Commits rather than files. The two are separate sources over the same folder when
  // both are wanted, so this is a choice about what THIS source is, not a modifier.
  const [gitHistory, setGitHistory] = useState(false);
  const [includeDiff, setIncludeDiff] = useState(false);
  // 2 MB was too small to be a useful default: it is a cap on ordinary files, since PDFs,
  // EPUBs and the other document formats are measured against DocumentMaxBytes instead, so
  // the only thing it was excluding was large text.
  const [maxFileMb, setMaxFileMb] = useState(20);
  const [include, setInclude] = useState('');
  const [exclude, setExclude] = useState('');
  const [busy, setBusy] = useState(false);

  // The same folder AND the same kind. Indexing a repository's files and its history is
  // deliberately two sources over one root, so warning about the second on the path
  // alone told the reader that the thing this feature exists for was a mistake.
  const wantedKind = gitHistory ? 'githistory' : 'workspace';
  const alreadyHere = corpus.sources.some((s) => s.rootPath === path && s.kind === wantedKind);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    try {
      // Undefined, not an empty array: an omitted filter means this source follows the
      // corpus, and an empty one means "none, whatever the corpus says". Sending [] for a
      // box nobody typed in would pin every new source against the default.
      await api.addSource(corpus.name, {
        workspacePath: path,
        // The file-shaped settings are not sent for a history source. They mean nothing
        // to a commit, and storing them would leave a source whose displayed filters
        // describe work it does not do.
        useGitignore: gitHistory ? undefined : useGitignore,
        maxFileBytes: gitHistory ? undefined : Math.round(maxFileMb * 1024 * 1024),
        includeGlobs: globList(include).length ? globList(include) : undefined,
        excludeGlobs: gitHistory || !globList(exclude).length ? undefined : globList(exclude),
        gitHistory: gitHistory || undefined,
        git: gitHistory ? { includeDiff } : undefined,
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
          <Notice tone="warn" className="-mt-2 mb-3.5 text-xs">
            {gitHistory
              ? 'This corpus already indexes that folder’s history. Adding it again indexes every commit twice.'
              : 'This corpus already indexes that folder. Adding it again indexes everything twice.'}
          </Notice>
        )}

        <label className="mb-3.5 flex items-start gap-2.5">
          <Checkbox
            checked={gitHistory}
            onCheckedChange={(v) => setGitHistory(v === true)}
            className="mt-0.5"
          />
          <span className="grid gap-0.5">
            <span className="text-sm leading-none font-semibold">Index its commit history</span>
            <span className="text-xs text-muted-foreground">
              One document per commit instead of one per file. To search both, add the
              folder twice: a commit and a file are different things to count.
            </span>
          </span>
        </label>

        {gitHistory && (
          <label className="mb-3.5 flex items-start gap-2.5">
            <Checkbox
              checked={includeDiff}
              onCheckedChange={(v) => setIncludeDiff(v === true)}
              className="mt-0.5"
            />
            <span className="grid gap-0.5">
              <span className="text-sm leading-none font-semibold">Include the diff</span>
              <span className="text-xs text-muted-foreground">
                Off, each commit is its message and which files it touched: measured over
                201 commits, about 2,000 characters each. On, it is the patch as well, and
                about thirteen times that.
              </span>
            </span>
          </label>
        )}

        {!gitHistory && (
          <Field label="Largest file (MB)" hint="Anything bigger is skipped and reported, not silently dropped.">
            <Input
              type="number"
              min={0.1}
              step="any"
              value={maxFileMb}
              onChange={(e) => setMaxFileMb(Number(e.target.value))}
            />
          </Field>
        )}

        <Field
          label="Only these (optional)"
          hint={gitHistory
            ? 'Paths, comma separated. Commits that touched them, and only their side of the diff.'
            : 'Globs, comma separated. Empty means everything not excluded.'}
        >
          <Input className="mono" value={include} onChange={(e) => setInclude(e.target.value)} placeholder="src/**, docs/**" />
        </Field>

        {!gitHistory && (
          <Field label="Never these (optional)" hint="Globs, comma separated. Applied after the include list.">
            <Input className="mono" value={exclude} onChange={(e) => setExclude(e.target.value)} placeholder="**/vendor/**, *.min.js" />
          </Field>
        )}

        {!gitHistory && (
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
        )}

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
          ? `Its ${source.fileCount.toLocaleString()} ${unitOf(source, source.fileCount)} ${source.fileCount === 1 ? 'leaves' : 'leave'} the index immediately, in every chunk set of ${corpus.name}.`
          : `It has no indexed ${unitOf(source, 0)}, so nothing leaves the index.`}
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

/**
 * What a full reindex is about to do, before it does it.
 *
 * It sat one click from Refresh with nothing between them, and the two are not
 * comparable: Refresh embeds what moved, this embeds everything. On the largest corpus
 * here that is 177,536 chunks against a normal refresh's none.
 *
 * Nothing is lost by running it, so there is no name to type — this asks about cost, not
 * about damage. What it does carry is the two routes people actually want when they reach
 * for it, because neither is this button: a different model needs a new chunk set, and new
 * or changed files need Refresh.
 */
function FullReindexModal({
  corpus,
  onClose,
  onError,
  onShowChunkSets,
  finalFocus,
}: {
  corpus: Corpus;
  onClose: () => void;
  onError: (e: unknown) => void;
  /** Close this and open the chunk sets, which is where a model change is made. */
  onShowChunkSets: () => void;
  /** Where focus goes when this closes; see Modal. */
  finalFocus: () => HTMLElement | null;
}) {
  const [queueing, setQueueing] = useState(false);

  // Every set, because the job names no chunk set and the indexer then runs each of them.
  // A corpus cut two ways costs both, which is not visible anywhere else on this screen.
  //
  // Chunks sum honestly: a chunk belongs to exactly one set. Files do NOT —
  // `corpus.fileCount` is the DEFAULT set's, by design, because that is what an
  // unqualified search reaches, and a file indexed in two sets is one file. So the total
  // here is chunks, and files are named per set, where the number means something.
  //
  // It is what the catalogue holds NOW, not a plan. A pending row has no chunks yet, a
  // failed one keeps the count it last had, and a file that changed will produce a
  // different number this time. The API has no planned figure to ask for, so the wording
  // says what this is — "it holds N chunks today" — rather than promising the work.
  const sets = corpus.chunkSets;
  const chunks = sets.reduce((n, s) => n + s.chunkCount, 0);

  // Failures are per (set, file) too, so the same file can appear in both. Counted, not
  // called files, once there is more than one set to conflate.
  const failed = sets.reduce((n, s) => n + s.failedCount, 0);

  // A git-history corpus counts commits and a mixed one counts documents. The rest of
  // this screen already says so; a hardcoded "files" here would contradict the row above
  // it.
  const unit = unitFor(corpus.sources, failed);

  const run = async (full: boolean) => {
    setQueueing(true);
    try {
      await api.reindex(corpus.name, full);
      onClose();
    } catch (e) {
      onError(e);
      setQueueing(false);
    }
  };

  return (
    <Modal title={`Full reindex of ${corpus.name}?`} onClose={onClose} width={560} finalFocus={finalFocus}>
      <p className="mt-0 text-sm">
        Every {unitFor(corpus.sources, 1)} is read again, whether or not it changed, and
        re-embedded if it can be read and chunked — in{' '}
        {sets.length === 1 ? 'this corpus’s chunk set' : <>all {sets.length} of this corpus’s chunk sets</>}.
        It holds <strong>{chunks.toLocaleString()}</strong> chunks today, and embedding is
        the slow part.
      </p>

      <p className="text-sm">
        One that is excluded, oversize or empty is recorded without being embedded, as it
        is on any pass.
      </p>

      <p className="text-sm">
        The corpus is not cleared up front: vectors are replaced one{' '}
        {unitFor(corpus.sources, 1)} at a time as the walk reaches it, so the rest stays
        searchable throughout. The one in hand does not — its old vectors are deleted
        before the new ones are written, so it is missing from search for that moment, and
        stays missing until a later pass if its embedding fails or the job is interrupted.
      </p>

      {/* The settings it will apply. They are set per chunk set and nowhere near this
          button, so a reindex was run to pick up a change without any way to see, here,
          what the change was. */}
      <div className="card p-3 grid gap-1.5 text-xs">
        <div className="dim">It will use each set as it stands now:</div>
        {sets.map((s) => (
          <div key={s.id} className="flex flex-wrap items-baseline gap-2">
            <span className="mono font-semibold">{corpus.name}:{s.name}</span>
            {s.isDefault && <Badge tone="accent">default</Badge>}
            <span className="mono dim">{s.embeddingModel}</span>
            <span className="dim">
              {s.chunkSize} / {s.chunkOverlap} overlap · {s.fileCount.toLocaleString()}{' '}
              {unitFor(corpus.sources, s.fileCount)} · {s.chunkCount.toLocaleString()} chunks
            </span>
          </div>
        ))}
        <div className="dim">Change any of it under Chunk sets, which re-chunks on its own.</div>
      </div>

      {failed > 0 && (
        <Notice tone="warn">
          {sets.length === 1 ? (
            <>{failed.toLocaleString()} {unit} failed last time.</>
          ) : (
            // Per (set, file), so one that failed in both sets is counted twice. Calling
            // that a count of documents would overstate how much is wrong.
            <>{failed.toLocaleString()} failures across {sets.length} chunk sets, which can be
              the same {unitFor(corpus.sources, 1)} more than once.</>
          )}{' '}
          A full reindex retries them, and so does Refresh — a failure has no fingerprint
          to skip on.
        </Notice>
      )}

      <Notice tone="accent">
        <strong>To move to another embedding model, this is not the button.</strong> A model
        is a different vector space, so it needs a new chunk set built alongside this one and
        promoted when it is complete.{' '}
        <button type="button" className="underline underline-offset-2" onClick={onShowChunkSets}>
          Add one under Chunk sets
        </button>
        .
      </Notice>

      <div className="mt-4 flex flex-wrap justify-end gap-2">
        <Button onClick={onClose} autoFocus>Cancel</Button>
        <Button disabled={queueing} onClick={() => void run(false)}>
          <RefreshCw />
          Refresh instead
        </Button>
        <Button variant="danger" disabled={queueing} onClick={() => void run(true)}>
          {queueing ? <Spinner /> : <RotateCcw />}
          Reindex everything
        </Button>
      </div>
    </Modal>
  );
}

// ── Jobs ────────────────────────────────────────────────────────────────────

/**
 * A scheduled refresh that found nothing to do.
 *
 * DEXICON__INDEXING__REFRESHMINUTES runs one per corpus per interval, and on an unchanged
 * tree every one of them indexes nothing. Ten identical "0 indexed, 30 skipped, 0 chunks"
 * cards is the list telling you at length that nothing happened, and the run that did
 * something is below the fold.
 */
function isNoOp(j: Job): boolean {
  return j.state === 'succeeded' && j.filesDone === 0 && j.chunksWritten === 0;
}

/** Consecutive no-ops collapse into one entry; anything that did something stays its own. */
function groupRuns(jobs: Job[]): ({ kind: 'job'; job: Job } | { kind: 'quiet'; jobs: Job[] })[] {
  const out: ({ kind: 'job'; job: Job } | { kind: 'quiet'; jobs: Job[] })[] = [];

  for (const job of jobs) {
    if (!isNoOp(job)) {
      out.push({ kind: 'job', job });
      continue;
    }
    const last = out[out.length - 1];
    if (last?.kind === 'quiet') last.jobs.push(job);
    else out.push({ kind: 'quiet', jobs: [job] });
  }

  // One on its own is not a run, and a summary of it would be longer than the card.
  return out.map((e) => (e.kind === 'quiet' && e.jobs.length === 1 ? { kind: 'job' as const, job: e.jobs[0] } : e));
}

export function JobsView({ corpora, live, onError }: { corpora: Corpus[]; live: Record<string, Progress>; onError: (e: unknown) => void }) {
  const [jobs, setJobs] = useState<Job[]>([]);
  const names = useMemo(() => Object.fromEntries(corpora.map((c) => [c.id, c.name])), [corpora]);
  // The job's own corpus, so its caption counts what that corpus counts.
  const sourcesOf = useMemo(
    () => Object.fromEntries(corpora.map((c) => [c.id, c.sources])), [corpora]);

  // Routine refreshes are dropped IN THE QUERY, not collapsed after the fact. The client
  // can only collapse what it fetched, and at one job per corpus per REFRESHMINUTES a
  // page of thirty is about an hour: on this instance 48 of the last 200 jobs did real
  // work and not one of them was in the most recent thirty, so the view was a summary of
  // nothing over the top of everything that mattered.
  const [routine, setRoutine] = useState(false);

  // Fast only while there is something to watch.
  //
  // This polled every four seconds for as long as the tab was open, whether or not
  // anything was running — and the progress STREAM already pushes a running job's
  // counters, which is what `live` holds. So the poll's job is to notice a run starting
  // or finishing, and `live` notices the start first: a job appearing there flips this
  // and the effect re-arms at the fast interval on the same tick.
  const busy =
    jobs.some((j) => j.state === 'running' || j.state === 'queued')
    || Object.values(live).some(isRunning);

  useEffect(() => {
    // Ignored after cleanup, results AND errors. This effect re-runs whenever the toggle
    // moves or `busy` changes, and the request it started is still in flight: without
    // this, an older reply lands after the newer one and paints the list it was NOT
    // asked for — tick the box and the routine runs appear, then vanish when the
    // activity-only reply arrives behind them, then come back on the next poll.
    let cancelled = false;

    const load = () => api.listJobs(routine ? 200 : 30, routine ? undefined : true)
      .then((next) => { if (!cancelled) setJobs(next); })
      .catch((e) => { if (!cancelled) onError(e); });

    void load();
    const id = setInterval(load, busy ? 4000 : 30000);
    return () => { cancelled = true; clearInterval(id); };
  }, [onError, routine, busy]);

  // The heading stays whichever way this goes: every other view keeps its title over an
  // empty state, and a screen that loses its name is a screen you cannot tell you are on.
  // The toggle sits with the heading on both branches, because the empty state is the
  // one place you most need to know that a filter is on: "no jobs yet" under a hidden
  // filter is the view lying about an empty database.
  const heading = (
    <div className="flex justify-between items-center gap-3">
      <h1 className="m-0 text-lg">Jobs</h1>
      <label className="dim flex items-center gap-1.5 text-xs">
        <input
          type="checkbox"
          checked={routine}
          onChange={(e) => setRoutine(e.target.checked)}
          aria-label="Show scheduled refreshes that found nothing to do"
        />
        Show routine refreshes
      </label>
    </div>
  );

  if (jobs.length === 0) {
    return (
      <div className="grid gap-4">
        {heading}
        <Empty
          title={routine ? 'No jobs yet' : 'Nothing has happened yet'}
          hint={
            routine
              ? 'Indexing runs appear here, newest first.'
              : 'Runs that indexed something, failed, or are still going appear here. '
                + 'Tick "Show routine refreshes" for the scheduled ones that found nothing to do.'
          }
        />
      </div>
    );
  }

  // gap-4 like every other list page. This one sat on gap-3, which put its heading
  // closer to the first card than the same heading is on Corpora or Documents.
  return (
    <div className="grid gap-4">
      {heading}
      {groupRuns(jobs).map((entry) => {
        if (entry.kind === 'quiet') {
          const newest = entry.jobs[0];
          const oldest = entry.jobs[entry.jobs.length - 1];
          const corpora = [...new Set(entry.jobs.map((j) => names[j.corpusId] ?? j.corpusId))];
          return (
            <div key={`quiet-${newest.id}`} className="card flex flex-wrap items-center gap-2.5 px-3.5 py-2.5">
              <span className="dim text-sm">
                {entry.jobs.length} scheduled {entry.jobs.length === 1 ? 'refresh' : 'refreshes'} found
                nothing to do
              </span>
              <Badge>{corpora.join(', ')}</Badge>
              <span className="flex-1" />
              <span className="dim text-xs" title={localTime(oldest.queuedUtc)}>
                {relativeTime(oldest.queuedUtc)} to {relativeTime(newest.finishedUtc ?? newest.queuedUtc)}
              </span>
            </div>
          );
        }

        const j = entry.job;
        // The live event where it is about THIS job, the poll otherwise. Matched on
        // jobId: the event has no `id`, so comparing one was false on every event and
        // a running job's counters never moved until the next poll.
        const merged = live[j.corpusId]?.jobId === j.id ? live[j.corpusId] : progressOf(j);
        return (
          <div key={j.id} className="card p-3.5">
            <div className="flex gap-2 items-center flex-wrap">
              <strong>{names[j.corpusId] ?? j.corpusId}</strong>
              {/* From the POLL, not the event: an IndexProgress has no state. The
                   effect below re-runs the moment `busy` goes false, so a run that has
                   just finished is re-read at once rather than at the idle interval. */}
              <Badge tone={stateTone(j.state)}>{j.state}</Badge>
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

            {isRunning(merged) && (
              <ProgressBar job={merged} sources={sourcesOf[j.corpusId]} />
            )}

            <div className="dim mt-1.5 text-xs flex gap-3.5 flex-wrap">
              <span>{merged.filesDone.toLocaleString()} indexed</span>
              <span>{merged.filesSkipped.toLocaleString()} skipped</span>
              {merged.filesFailed > 0 && <span className="text-[var(--danger-text)]">{merged.filesFailed.toLocaleString()} failed</span>}
              <span>{merged.chunksWritten.toLocaleString()} chunks</span>
            </div>

            {merged.error && (
              <p
                className={cn(
                  'mt-2 mb-0 text-xs',
                  // A job that is alive but achieving nothing is not a failed one, and
                  // colouring it red says it is.
                  j.state === 'degraded' ? 'text-[var(--warn-text)]' : 'text-[var(--danger-text)]',
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
  const [corpora, setCorpora] = useState<Corpus[]>([]);
  const [creating, setCreating] = useState(false);
  const [mapping, setMapping] = useState<TokenSummary | null>(null);
  const [issued, setIssued] = useState<Awaited<ReturnType<typeof api.createToken>> | null>(null);

  const load = useCallback(async () => {
    try {
      setTokens(await api.listTokens());
      // Needed to render a mapping as names rather than ids, and to offer the ticks.
      setCorpora(await api.listCorpora());
    } catch (e) { onError(e); }
  }, [onError]);

  const nameOf = useCallback(
    (id: string) => corpora.find((c) => c.id === id)?.name ?? id,
    [corpora],
  );

  useEffect(() => { void load(); }, [load]);

  return (
    <div className="grid gap-5">
      <h1 className="m-0 text-lg">Access</h1>

      <section>
        <div className="flex justify-between items-center mb-3">
          <h2 className="mt-0 mx-0 mb-0 text-base">API keys</h2>
          <Button variant="primary" onClick={() => setCreating(true)}><Plus />New key</Button>
        </div>

        {tokens.length === 0 ? (
          <Empty title="No keys" hint="A key is how an agent authenticates. Create one per agent, then choose what it can reach." />
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

                {/* Its own row, because it is the thing that changes most often. No rows
                    means every corpus, which is not the same as none: a key that should
                    reach nothing is revoked. */}
                <div className="basis-[100%] flex flex-wrap items-center gap-1.5 text-xs">
                  <span className="dim">Reaches</span>
                  {t.corpusIds.length === 0
                    ? <Badge>every corpus</Badge>
                    : t.corpusIds.map((id) => <Badge key={id}>{nameOf(id)}</Badge>)}
                  {!t.revokedUtc && (
                    <Button onClick={() => setMapping(t)}>Change</Button>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}
        <p className="dim text-xs mt-2">
          Secrets are shown once, at creation, and are stored only as a PBKDF2 hash. There is no way to
          recover one. Changing what a key reaches takes effect on that agent's next call; granting
          ingest is listed to it when its client next reconnects.
        </p>
      </section>

      {mapping && (
        <CorpusMappingModal
          token={mapping}
          corpora={corpora}
          onClose={() => setMapping(null)}
          onSaved={async () => { setMapping(null); await load(); }}
          onError={onError}
        />
      )}

      {creating && (
        <CreateTokenModal
          corpora={corpora}
          onClose={() => setCreating(false)}
          onCreated={async (t) => { setCreating(false); setIssued(t); await load(); }}
          onError={onError}
        />
      )}

      {issued && (
        <Modal title="Token created" onClose={() => setIssued(null)} width={680}>
          <Notice tone="warn" className="mb-3.5">
            <strong>Copy it now.</strong> This is the only time it will be shown. It is stored as a hash and cannot be recovered.
          </Notice>
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

/**
 * Which corpora a key may reach.
 *
 * Replaces the mapping outright rather than patching it, because the whole set of ticks is
 * edited at once and a partial update would need a way to say "leave that one alone" that
 * is indistinguishable from "untick it".
 */
function CorpusMappingModal({ token, corpora, onClose, onSaved, onError }: {
  token: TokenSummary;
  corpora: Corpus[];
  onClose: () => void;
  onSaved: () => Promise<void>;
  onError: (e: unknown) => void;
}) {
  const [picked, setPicked] = useState<string[]>(token.corpusIds);
  const [busy, setBusy] = useState(false);

  return (
    <Modal title={`What ${token.name} can reach`} onClose={onClose}>
      <form
        onSubmit={async (e) => {
          e.preventDefault();
          setBusy(true);
          try { await api.setTokenCorpora(token.id, picked); await onSaved(); }
          catch (err) { onError(err); setBusy(false); }
        }}
      >
        {corpora.length === 0 ? (
          <Empty title="No corpora" hint="Create one first, then come back and tick it." />
        ) : (
          <div className="flex gap-1.5 flex-wrap">
            {corpora.map((c) => (
              <Chip
                key={c.id}
                type="button"
                active={picked.includes(c.id)}
                onClick={() => setPicked((prev) =>
                  prev.includes(c.id) ? prev.filter((x) => x !== c.id) : [...prev, c.id])}
              >
                {picked.includes(c.id) && <Check />}
                {c.name}
              </Chip>
            ))}
          </div>
        )}

        <p className="dim text-xs mt-3 mb-0">
          {picked.length === 0
            ? 'Nothing ticked means every corpus, including ones created later. To stop a key reaching anything, revoke it.'
            : `${picked.length} of ${corpora.length}. The agent sees the change on its next call.`}
        </p>

        <div className="flex gap-2 justify-end mt-4">
          <Button type="button" onClick={onClose}>Cancel</Button>
          <Button variant="primary" type="submit" disabled={busy}>
            {busy ? <Spinner /> : null} Save
          </Button>
        </div>
      </form>
    </Modal>
  );
}

function CreateTokenModal({ onClose, onCreated, onError, corpora }: {
  onClose: () => void;
  onCreated: (t: Awaited<ReturnType<typeof api.createToken>>) => Promise<void>;
  onError: (e: unknown) => void;
  corpora: Corpus[];
}) {
  const [name, setName] = useState('');
  const [scopes, setScopes] = useState<string[]>(['search']);
  const [picked, setPicked] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);

  const toggle = (s: string) => setScopes((prev) => (prev.includes(s) ? prev.filter((x) => x !== s) : [...prev, s]));

  return (
    <Modal title="New key" onClose={onClose}>
      <form
        onSubmit={async (e) => {
          e.preventDefault();
          setBusy(true);
          try { await onCreated(await api.createToken(name, scopes, picked)); }
          catch (err) { onError(err); setBusy(false); }
        }}
      >
        <Field label="Name" hint="Which agent this is for. It appears in the audit log.">
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="claude-code" autoFocus />
        </Field>
        <Field
          label="Scopes"
          hint="search reads; ingest lets the agent trigger a reindex, and is what puts index_refresh on its MCP tool list. Administration is the password's, so a key cannot hold it."
        >
          <div className="flex gap-1.5 flex-wrap">
            {['search', 'ingest'].map((s) => (
              <Chip key={s} type="button" active={scopes.includes(s)} onClick={() => toggle(s)}>
                {scopes.includes(s) && <Check />}
                {s}
              </Chip>
            ))}
          </div>
        </Field>
        <Field
          label="Corpora"
          hint="What this agent can search. Tick none for every corpus. Changeable afterwards without reissuing the key."
        >
          {corpora.length === 0 ? (
            <p className="dim text-xs m-0">No corpora yet. The key will reach every corpus you create.</p>
          ) : (
            <div className="flex gap-1.5 flex-wrap">
              {corpora.map((c) => (
                <Chip
                  key={c.id}
                  type="button"
                  active={picked.includes(c.id)}
                  onClick={() => setPicked((prev) =>
                    prev.includes(c.id) ? prev.filter((x) => x !== c.id) : [...prev, c.id])}
                >
                  {picked.includes(c.id) && <Check />}
                  {c.name}
                </Chip>
              ))}
            </div>
          )}
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
        <h2 className="mt-0 mx-0 mb-3 text-base">Appearance</h2>
        {/* One-of-N, so the same control as the search mode and the file filter. As three
            Chips it announced three independent toggles, two of them unpressed, which is
            not what choosing a theme is. */}
        <Segmented
          label="Appearance"
          value={theme}
          onChange={setTheme}
          options={[
            { value: 'auto', label: 'auto', title: 'Follow the operating system' },
            { value: 'light', label: 'light' },
            { value: 'dark', label: 'dark' },
          ]}
        />
      </section>

      <section className="card p-3.5">
        <h2 className="mt-0 mx-0 mb-3 text-base">Dependencies</h2>
        {/* Read-only: endpoints come from the environment. Making them editable here
            would mean storing them, and an endpoint is not a user preference. */}
        <div className="grid gap-2 text-sm">
          <Row label="Qdrant"><span className="mono">{health?.qdrant.endpoint ?? '—'}</span> <Badge tone={health?.qdrant.reachable ? 'ok' : 'danger'}>{health?.qdrant.reachable ? 'reachable' : 'unreachable'}</Badge></Row>
          <Row label="Embeddings">
            <span className="mono">{health?.ollama.endpoint ?? '—'}</span>{' '}
            <Badge tone={health?.ollama.reachable ? 'ok' : 'danger'}>{health?.ollama.reachable ? 'reachable' : 'unreachable'}</Badge>{' '}
            {health?.ollama.provider && <Badge>{health.ollama.provider}</Badge>}
          </Row>
          {/* "Model" implied the deployment had one. It has a default for new corpora;
              every chunk set records the model it was built with and keeps it. */}
          <Row label="New corpora">
            <span className="mono">{health?.ollama.model ?? '—'}</span> · {health?.ollama.dimensions ?? 0}d
          </Row>
          <Row label="Corpora">{health?.corpora ?? 0}</Row>
        </div>

        <div className="mt-3">
          {/* The label stays put while it runs. Swapping it for a bare spinner left an
              unlabelled square where the button had been, and a screen reader reading a
              button with no name. */}
          <Button disabled={checking} onClick={() => void checkConnectivity()}>
            {checking ? <Spinner /> : null}
            {checking ? 'Checking…' : 'Check connectivity'}
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
