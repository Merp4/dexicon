import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { RefreshCw } from 'lucide-react';
import { api, type SearchLogItem, type SearchLogPage } from './api';
import {
  Badge, Button, Empty, Input, Notice, Segmented, Select, SelectItem, Spinner, localTime, relativeTime,
} from './ui';
import { count } from './lib/units';

/**
 * The searches that ran, newest first: which key, through which interface, what was asked and the result. A search
 * refused before it ran (an unknown corpus) is not listed. The text is what callers sent, and the context hook sends each
 * prompt it handles, so the screen states the retention period and whether the text is stored.
 */

export type Which = 'all' | 'nothing';

const SURFACES = ['mcp', 'api', 'context', 'other'] as const;
const ANY = 'any';

const SURFACE_LABEL: Record<string, string> = {
  Mcp: 'MCP tool',
  Api: 'REST',
  Context: 'Context',
  Other: 'Unknown',
};

const PAGE = 50;

/** What a request for a page carries: only the filters that are set, so an unset one asks for no narrowing. */
export function queryFor(which: Which, surface: string, text: { key: string; corpus: string }) {
  return {
    limit: PAGE,
    ...(which === 'nothing' ? { noResults: true } : {}),
    ...(surface !== ANY ? { surface } : {}),
    ...(text.key ? { key: text.key } : {}),
    ...(text.corpus ? { corpus: text.corpus } : {}),
  };
}

/** The text the server kept, or what it kept instead when it kept none. */
export function whatWasAsked(item: Pick<SearchLogItem, 'query' | 'queryChars'>): string {
  if (item.query == null) return `Text not stored (${count(item.queryChars, 'character')})`;
  return item.query.length < item.queryChars ? `${item.query}…` : item.query;
}

/** Scores are in the units of the mode that ran, so a figure is shown and never compared across rows. */
export function score(item: Pick<SearchLogItem, 'topScore'>): string {
  return item.topScore == null ? '' : item.topScore.toFixed(3);
}

export function SearchesView({ onError }: { onError: (e: unknown) => void }) {
  const [which, setWhich] = useState<Which>('all');
  const [surface, setSurface] = useState(ANY);

  // The two text filters are applied when the form is submitted, not on each keystroke: each change is a
  // query over the whole log.
  const [keyDraft, setKeyDraft] = useState('');
  const [corpusDraft, setCorpusDraft] = useState('');
  const [text, setText] = useState({ key: '', corpus: '' });

  const [first, setFirst] = useState<SearchLogPage | null>(null);
  const [more, setMore] = useState<SearchLogItem[]>([]);
  const [next, setNext] = useState<number | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  const [reload, setReload] = useState(0);

  // Which answer the screen is waiting for. A page of older searches that arrives after the filters changed was asked
  // under the old ones, and is dropped.
  const generation = useRef(0);

  const filters = useCallback(() => queryFor(which, surface, text), [which, surface, text]);

  useEffect(() => {
    let cancelled = false;
    generation.current += 1;
    setFirst(null);
    setMore([]);
    api.listSearches(filters())
      .then((page) => {
        if (cancelled) return;
        setFirst(page);
        setNext(page.nextBefore ?? null);
      })
      .catch((e) => { if (!cancelled) onError(e); });
    return () => { cancelled = true; };
  }, [filters, reload, onError]);

  async function loadMore() {
    if (next == null) return;
    setLoadingMore(true);
    const asked = generation.current;
    try {
      const page = await api.listSearches({ ...filters(), before: next });
      if (asked !== generation.current) return;
      setMore((rows) => [...rows, ...page.items]);
      setNext(page.nextBefore ?? null);
    } catch (e) {
      onError(e);
    } finally {
      setLoadingMore(false);
    }
  }

  function applyText(e: FormEvent) {
    e.preventDefault();
    setText({ key: keyDraft.trim(), corpus: corpusDraft.trim() });
  }

  const rows = first ? [...first.items, ...more] : null;
  const settings = first?.settings;
  const filtered = which !== 'all' || surface !== ANY || text.key !== '' || text.corpus !== '';

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="m-0 text-lg font-semibold">Search history</h1>
          <p className="m-0 mt-1 max-w-[70ch] text-sm text-muted-foreground">
            Searches that have run, newest first. No results lists the ones that returned nothing.
          </p>
        </div>
        <Button onClick={() => setReload((n) => n + 1)}>
          <RefreshCw />
          Refresh
        </Button>
      </div>

      {settings && !settings.enabled && (
        <Notice tone="warn">
          The search log is switched off (<code>DEXICON__SEARCHLOG__ENABLED</code>), so new searches are not recorded.
          Existing entries are still deleted when they pass the retention period, and are kept indefinitely if it is 0 days or less.
        </Notice>
      )}
      {settings?.enabled && (
        <Notice tone="neutral">
          {settings.retentionDays <= 0 ? 'Kept indefinitely.' : `Kept for ${count(settings.retentionDays, 'day')}.`}{' '}
          {settings.storesQueryText
            ? 'Query text is stored, including each prompt the context hook sends, and is included in backups of the catalogue.'
            : 'Query text is not stored, only its length.'}
          {settings.dropped > 0 && <> {count(settings.dropped, 'search', 'searches')} could not be recorded since the server started.</>}
        </Notice>
      )}

      <div className="flex flex-wrap items-end gap-3">
        <Segmented<Which>
          label="Which searches"
          value={which}
          onChange={setWhich}
          options={[
            { value: 'all', label: 'All' },
            { value: 'nothing', label: 'No results' },
          ]}
        />
        <div className="w-40">
          <Select value={surface} onValueChange={setSurface} aria-label="Interface">
            <SelectItem value={ANY}>Any interface</SelectItem>
            {SURFACES.map((s) => (
              <SelectItem key={s} value={s}>{SURFACE_LABEL[s[0].toUpperCase() + s.slice(1)] ?? s}</SelectItem>
            ))}
          </Select>
        </div>
        <form className="flex flex-wrap items-center gap-2" onSubmit={applyText}>
          <Input
            aria-label="Key name"
            placeholder="Key name"
            className="w-40"
            value={keyDraft}
            onChange={(e) => setKeyDraft(e.target.value)}
          />
          <Input
            aria-label="Corpus name"
            placeholder="Corpus contains"
            className="w-44"
            value={corpusDraft}
            onChange={(e) => setCorpusDraft(e.target.value)}
          />
          <Button type="submit">Filter</Button>
        </form>
      </div>

      {rows === null && (
        <p className="m-0 flex items-center gap-2 text-sm text-muted-foreground"><Spinner /> Loading…</p>
      )}

      {rows !== null && rows.length === 0 && (
        filtered
          ? <Empty title="No searches match" hint="Remove a filter to see more." />
          : <Empty
              title="No searches recorded"
              hint={settings?.enabled === false
                ? 'The search log is switched off.'
                : 'Searches from the Search screen, agents and the context hook appear here.'}
            />
      )}

      {rows !== null && rows.length > 0 && first && (
        <>
          <p className="m-0 text-sm text-muted-foreground" aria-live="polite">
            {count(first.matching, 'search', 'searches')} {filtered ? (first.matching === 1 ? 'matches' : 'match') : 'recorded'}.
            {first.oldestUtc && <> The oldest was <time title={localTime(first.oldestUtc)}>{relativeTime(first.oldestUtc)}</time>.</>}
          </p>
          <div className="overflow-x-auto rounded-lg border border-border bg-card">
            <table className="w-full min-w-[900px] border-collapse text-left text-sm">
              <thead>
                <tr className="border-b border-border text-xs uppercase tracking-wide text-muted-foreground">
                  <th scope="col" className="px-3 py-2 font-medium">When</th>
                  <th scope="col" className="px-3 py-2 font-medium">Key</th>
                  <th scope="col" className="px-3 py-2 font-medium">Interface</th>
                  <th scope="col" className="px-3 py-2 font-medium">Corpora</th>
                  <th scope="col" className="px-3 py-2 font-medium">Asked</th>
                  <th scope="col" className="px-3 py-2 text-right font-medium">Hits</th>
                  <th scope="col" className="px-3 py-2 text-right font-medium">Top score</th>
                  <th scope="col" className="px-3 py-2 text-right font-medium">Took</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.id} className="border-b border-border last:border-b-0 align-top">
                    <td className="whitespace-nowrap px-3 py-2">
                      <time title={localTime(r.atUtc)}>{relativeTime(r.atUtc)}</time>
                    </td>
                    <td className="px-3 py-2">{r.keyName}</td>
                    <td className="px-3 py-2">
                      <Badge tone={r.surface === 'Other' ? 'warn' : 'neutral'}>{SURFACE_LABEL[r.surface] ?? r.surface}</Badge>
                    </td>
                    <td className="px-3 py-2 [overflow-wrap:anywhere]">{r.corpora}</td>
                    <td className="max-w-[34ch] px-3 py-2 [overflow-wrap:anywhere]">
                      <span className={r.query == null ? 'text-muted-foreground' : undefined}>{whatWasAsked(r)}</span>
                    </td>
                    <td className="px-3 py-2 text-right">
                      {r.hits === 0 ? <Badge tone="warn">none</Badge> : r.hits}
                    </td>
                    <td className="px-3 py-2 text-right tabular-nums" title={`Score of the first hit, in the units of the ${r.mode} mode`}>
                      {score(r)}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                      {r.tookMs} ms
                      {r.degraded && <Badge tone="warn" className="ml-1.5">keyword only</Badge>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {next != null && (
            <div className="flex justify-center">
              <Button disabled={loadingMore} onClick={() => void loadMore()}>
                {loadingMore ? 'Loading…' : 'Older searches'}
              </Button>
            </div>
          )}
        </>
      )}
    </div>
  );
}
