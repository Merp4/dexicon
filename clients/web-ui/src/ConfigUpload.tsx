import { useRef, useState, type ChangeEvent } from 'react';
import { Upload } from 'lucide-react';
import { api, type ConfigExport, type ConfigImportResult, type ImportItem } from './api';
import { Badge, Button, ErrorBanner, Modal, Notice, Spinner, type Tone } from './ui';

/**
 * A configuration file read into this instance. The file is checked first, which changes nothing and lists what applying it
 * would do; applying is a second step that asks. It adds corpora and sources and updates the filters of those that are there. It
 * deletes none of them, though a narrower filter removes the files it excludes from the index. See D-54.
 */

const TONE: Record<string, Tone> = {
  create: 'accent',
  update: 'accent',
  unchanged: 'neutral',
  'not imported': 'neutral',
  extra: 'neutral',
  refused: 'warn',
  failed: 'danger',
};

/** The counts as one line, in the order a person reads them: what changes, then what does not, then what went wrong. */
export function summary(counts: Record<string, number>): string {
  const order = ['create', 'update', 'unchanged', 'not imported', 'extra', 'refused', 'failed'];
  const known = order.filter((k) => counts[k]).map((k) => `${counts[k]} ${k}`);
  const other = Object.keys(counts).filter((k) => !order.includes(k)).map((k) => `${counts[k]} ${k}`);
  return [...known, ...other].join(', ') || 'nothing in the file';
}

export function ConfigUpload() {
  const [loaded, setLoaded] = useState<{ name: string; body: ConfigExport } | null>(null);
  const [result, setResult] = useState<ConfigImportResult | null>(null);
  const [busy, setBusy] = useState<'check' | 'apply' | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [confirming, setConfirming] = useState(false);
  const input = useRef<HTMLInputElement>(null);

  async function choose(e: ChangeEvent<HTMLInputElement>) {
    const picked = e.target.files?.[0];
    setResult(null);
    setError(null);
    setLoaded(null);
    if (!picked) return;

    try {
      const body: unknown = JSON.parse(await picked.text());
      if (typeof body !== 'object' || body === null || Array.isArray(body)) throw new Error('not an object');
      setLoaded({ name: picked.name, body: body as ConfigExport });
    } catch {
      setError(new Error(`${picked.name} is not a JSON configuration file.`));
    }
  }

  async function run(apply: boolean) {
    if (!loaded) return;
    setBusy(apply ? 'apply' : 'check');
    setError(null);
    try {
      setResult(await api.importConfig(loaded.body, apply));
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  }

  const checked = result !== null && !result.applied;
  const indexed = (result?.items ?? []).map((item, at) => ({ item, at }));
  const changes = indexed.filter(({ item }) => item.outcome !== 'unchanged');
  const unchanged = indexed.filter(({ item }) => item.outcome === 'unchanged');
  const queues = result?.items.filter((i) => i.outcome === 'create' || i.outcome === 'update').length ?? 0;

  return (
    <section className="card p-3.5">
      <h2 className="mt-0 mx-0 mb-3 text-base">Import configuration</h2>
      <p className="mt-0 mb-3 text-sm text-muted-foreground">
        Read a configuration file into this instance. Checking it changes nothing and lists what applying would do. Applying adds
        the corpora and sources that are missing and updates the filters of those that are there. It deletes no corpus, source or
        chunk set, but a narrower filter removes the files it excludes from the index at the refresh. Chunk sets other than a
        new corpus's default, keys and model profiles in the file are ignored.
      </p>

      <input
        ref={input}
        type="file"
        accept=".json,application/json"
        aria-label="Configuration file"
        disabled={busy !== null}
        className="mb-3 block text-sm"
        onChange={(e) => void choose(e)}
      />

      <div className="flex flex-wrap gap-2">
        <Button disabled={!loaded || busy !== null} onClick={() => void run(false)}>
          {busy === 'check' ? <Spinner /> : <Upload />}
          {busy === 'check' ? 'Checking…' : 'Check the file'}
        </Button>
        <Button variant="danger" disabled={!checked || busy !== null} onClick={() => setConfirming(true)}>
          Apply
        </Button>
      </div>

      <ErrorBanner error={error} onDismiss={() => setError(null)} />

      {result && (
        <div className="mt-3 grid gap-2">
          <Notice tone={result.applied ? 'ok' : 'neutral'}>
            {result.applied ? 'Applied' : 'Checked, nothing changed'}: {summary(result.counts)}.
          </Notice>
          {/* What changes or went wrong comes first. An instance of a hundred sources would otherwise bury two changes
              under the rows that say nothing. */}
          <ul aria-label="What the file does" className="m-0 grid list-none gap-1.5 p-0 text-sm">
            {changes.map(({ item, at }) => (
              <ItemRow key={`${at}-${item.kind}-${item.name}`} item={item} />
            ))}
          </ul>
          {unchanged.length > 0 && (
            <details className="text-sm">
              <summary className="cursor-pointer text-muted-foreground">{unchanged.length} unchanged</summary>
              <ul aria-label="Unchanged" className="m-0 mt-2 grid list-none gap-1.5 p-0">
                {unchanged.map(({ item, at }) => (
                  <ItemRow key={`${at}-${item.kind}-${item.name}`} item={item} />
                ))}
              </ul>
            </details>
          )}
        </div>
      )}

      {confirming && (
        <Modal title="Apply this file?" onClose={() => setConfirming(false)}>
          <p className="mt-0 text-sm">
            {queues} {queues === 1 ? 'item makes' : 'items make'} a change. Each added or changed source, or corpus default filter,
            queues a refresh as if it were made by hand, which can mean a lot of indexing on a large instance. A narrower filter
            removes the files it excludes from the index. No corpus, source or chunk set is deleted. The import cannot be undone
            as a whole: delete what it created the usual way, and revert an update by applying the earlier file.
          </p>
          <div className="flex justify-end gap-2">
            <Button onClick={() => setConfirming(false)}>Cancel</Button>
            <Button variant="danger" onClick={() => { setConfirming(false); void run(true); }}>Apply</Button>
          </div>
        </Modal>
      )}
    </section>
  );
}

function ItemRow({ item }: { item: ImportItem }) {
  return (
    <li className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5 rounded-md border border-border px-2.5 py-1.5">
      <Badge tone={TONE[item.outcome] ?? 'neutral'}>{item.outcome}</Badge>
      <span className="text-xs uppercase tracking-wide text-muted-foreground">{item.kind}</span>
      <span className="break-words font-medium [overflow-wrap:anywhere]">{item.name}</span>
      {item.detail && <span className="w-full text-muted-foreground [overflow-wrap:anywhere]">{item.detail}</span>}
    </li>
  );
}
