import { useRef, useState, type ChangeEvent } from 'react';
import { Upload } from 'lucide-react';
import { api, type ConfigExport, type ConfigImportResult, type ImportItem } from './api';
import { Badge, Button, ErrorBanner, Modal, Notice, Spinner, type Tone } from './ui';

/**
 * A configuration file read into this instance. The file is checked first, which changes nothing and lists what applying it
 * would do; applying is a second step that asks. It adds corpora and sources and updates the filters of those that are there, and
 * it never removes one. See D-54.
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
  const queues = result?.items.filter((i) => i.outcome === 'create' || i.outcome === 'update').length ?? 0;

  return (
    <section className="card p-3.5">
      <h2 className="mt-0 mx-0 mb-3 text-base">Import configuration</h2>
      <p className="mt-0 mb-3 text-sm text-muted-foreground">
        Read a configuration file into this instance. Checking it changes nothing and lists what applying would do. Applying adds
        the corpora and sources that are missing and updates the filters of those that are there. It removes nothing, and it does
        not read chunk sets beyond a new corpus's default, keys or model profiles.
      </p>

      <input
        ref={input}
        type="file"
        accept=".json,application/json"
        aria-label="Configuration file"
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
          <ul aria-label="What the file does" className="m-0 grid list-none gap-1.5 p-0 text-sm">
            {result.items.map((item, i) => (
              <ItemRow key={`${i}-${item.kind}-${item.name}`} item={item} />
            ))}
          </ul>
        </div>
      )}

      {confirming && (
        <Modal title="Apply this file?" onClose={() => setConfirming(false)}>
          <p className="mt-0 text-sm">
            {queues} {queues === 1 ? 'item makes' : 'items make'} a change, and each change queues the same refresh as making it by hand,
            which on a large instance is a lot of indexing. Nothing is removed. It cannot be undone as a whole: what it makes is removed
            through the removal paths, and an update is changed back by applying the earlier file.
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
