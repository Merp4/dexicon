import { useEffect, useState } from 'react';
import { Check, Trash2, X } from 'lucide-react';
import { api, type Proposal } from './api';
import {
  Badge, Button, Empty, ErrorBanner, Modal, Notice, Segmented, Spinner, localTime, relativeTime,
  type Tone,
} from './ui';
import { count } from './lib/units';

/**
 * Removals agents have asked for, to be decided here.
 *
 * Each request is what a key holding `propose` asked for, with the reason it gave. What would go
 * is the server's figure, worked out when this list was read, and not the agent's description.
 * Approving removes the target for good and asks first, since nothing here can undo it;
 * rejecting leaves everything as it is and can be asked for again.
 */

type Tab = 'waiting' | 'decided';

const POLL_MS = 30_000;

const KIND: Record<string, string> = {
  source: 'Source',
  chunk_set: 'Chunk set',
  document: 'Document',
  corpus: 'Corpus',
};

/** What it is about, as a sentence: the verb a person would use for that kind of removal. */
export function headline(p: Proposal): string {
  switch (p.kind) {
    case 'source': return `Remove the source ${p.target} from ${p.corpusName}`;
    case 'chunk_set': return `Delete the chunk set ${p.target} from ${p.corpusName}`;
    case 'document': return `Detach the document ${p.target} from ${p.corpusName}`;
    default: return `Delete the corpus ${p.corpusName}`;
  }
}

/**
 * What approving would take, from the figures the server worked out. A history source's units are
 * commits, which its label says (`history:`), so it is counted as such.
 */
export function takes(p: Proposal): string | null {
  const f = p.facts;
  if (!f) return null;

  const unit = p.target.startsWith('history:') ? 'commit' : 'file';
  const chunks = count(f.chunks, 'chunk');
  const sets = count(f.chunkSets, 'chunk set');

  switch (p.kind) {
    case 'source': return `${count(f.files, unit)} and ${chunks}, across ${sets}`;
    case 'chunk_set': return `${chunks}, from ${count(f.files, 'file')}`;
    case 'document': return `${chunks}, across ${sets}`;
    default: return `${count(f.sources, 'source')}, ${count(f.files, 'file')} and ${chunks}, across ${sets}`;
  }
}

const OUTCOME_TONE: Record<string, Tone> = { approved: 'ok', rejected: 'neutral', failed: 'warn' };

export function ApprovalsView({ onError, onDecided }: {
  onError: (e: unknown) => void;
  /** After a request is approved or rejected: what was removed changes the corpora and the count. */
  onDecided: () => void;
}) {
  const [tab, setTab] = useState<Tab>('waiting');
  const [rows, setRows] = useState<Proposal[] | null>(null);
  const [confirming, setConfirming] = useState<Proposal | null>(null);
  const [rejecting, setRejecting] = useState<string | null>(null);
  // A refusal belongs to the row it came from, not to a banner at the top of the page.
  const [refused, setRefused] = useState<Record<string, unknown>>({});
  const [reload, setReload] = useState(0);

  // The other tab's rows are not these, so they are not shown while these load.
  useEffect(() => { setRows(null); }, [tab]);

  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      try {
        const listed = await api.listProposals(tab === 'decided');
        if (!cancelled) setRows(listed);
      } catch (e) {
        if (!cancelled) onError(e);
      }
    };
    void load();
    const id = setInterval(load, POLL_MS);
    return () => { cancelled = true; clearInterval(id); };
  }, [tab, reload, onError]);

  const decided = () => { setReload((n) => n + 1); onDecided(); };

  async function reject(p: Proposal) {
    setRejecting(p.id);
    setRefused(({ [p.id]: _gone, ...rest }) => rest);
    try {
      await api.rejectProposal(p.id);
      decided();
    } catch (e) {
      setRefused((prev) => ({ ...prev, [p.id]: e }));
    } finally {
      setRejecting(null);
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="m-0 text-lg font-semibold">Approvals</h1>
          <p className="m-0 mt-1 max-w-[70ch] text-sm text-muted-foreground">
            Removals that agents holding the propose scope have asked for. Approving removes the target
            for good. Rejecting leaves it, and the agent can ask again.
          </p>
        </div>
        <Segmented<Tab>
          label="Which requests"
          value={tab}
          onChange={setTab}
          options={[
            { value: 'waiting', label: 'Waiting' },
            { value: 'decided', label: 'Decided' },
          ]}
        />
      </div>

      {rows === null && (
        <p className="m-0 flex items-center gap-2 text-sm text-muted-foreground"><Spinner /> Loading…</p>
      )}

      {rows !== null && rows.length === 0 && (
        tab === 'waiting'
          ? <Empty
              title="Nothing is waiting"
              hint="When an agent holding the propose scope asks for something to be removed, it appears here. Grant the scope to a key on the Access page."
            />
          : <Empty title="Nothing has been decided yet" />
      )}

      {rows !== null && rows.length > 0 && (
        <ul aria-label={tab === 'waiting' ? 'Requests waiting for a decision' : 'Requests already decided'}
          className="m-0 flex list-none flex-col gap-3 p-0">
          {rows.map((p) => (
            <li key={p.id} className="flex flex-col gap-2.5 rounded-lg border border-border bg-card p-4">
              <div className="flex flex-wrap items-center gap-2">
                <Badge>{KIND[p.kind] ?? p.kind}</Badge>
                <strong className="min-w-0 break-words">{headline(p)}</strong>
                {tab === 'decided' && (
                  <Badge tone={OUTCOME_TONE[p.status] ?? 'neutral'}>{p.status}</Badge>
                )}
              </div>

              {tab === 'waiting' && takes(p) && (
                <p className="m-0 text-sm">
                  <span className="text-muted-foreground">Would take </span>{takes(p)}.
                </p>
              )}

              {tab === 'waiting' && p.gone && (
                <Notice tone="warn">
                  It is no longer there. Approving records the request as failed and removes nothing.
                </Notice>
              )}
              {tab === 'waiting' && p.facts?.blocker && (
                <Notice tone="warn">
                  It cannot be approved now: {p.facts.blocker}.
                </Notice>
              )}

              <blockquote className="m-0 border-l-2 border-border pl-3 text-sm">
                <span className="block text-xs uppercase tracking-wide text-muted-foreground">
                  {p.keyName} said
                </span>
                {p.reason}
              </blockquote>

              <p className="m-0 text-xs text-muted-foreground">
                {tab === 'waiting'
                  ? <>Asked <time title={localTime(p.createdUtc)}>{relativeTime(p.createdUtc)}</time></>
                  : <>Decided <time title={localTime(p.decidedUtc)}>{relativeTime(p.decidedUtc)}</time>
                    {p.status === 'approved' && ': it was removed'}
                    {p.status === 'rejected' && ': it stays'}
                    {p.status === 'failed' && p.error && `: ${p.error}`}</>}
              </p>

              {refused[p.id] != null && (
                <ErrorBanner
                  error={refused[p.id]}
                  onDismiss={() => setRefused(({ [p.id]: _gone, ...rest }) => rest)}
                />
              )}

              {tab === 'waiting' && (
                <div className="flex flex-wrap justify-end gap-2">
                  <Button
                    aria-label={`Reject: ${headline(p)}`}
                    disabled={rejecting === p.id}
                    onClick={() => void reject(p)}
                  >
                    <X />
                    {rejecting === p.id ? 'Rejecting…' : 'Reject'}
                  </Button>
                  <Button
                    variant="danger"
                    aria-label={`Approve: ${headline(p)}`}
                    disabled={Boolean(p.facts?.blocker)}
                    onClick={() => setConfirming(p)}
                  >
                    <Check />
                    Approve
                  </Button>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}

      {confirming && (
        <ApproveModal
          proposal={confirming}
          onClose={() => setConfirming(null)}
          onApproved={() => { setConfirming(null); decided(); }}
        />
      )}
    </div>
  );
}

/**
 * Approving, asked first. It runs the same removal the delete buttons run, which nothing here can
 * undo, so the figures are said again where the decision is made. A refusal from the server is
 * shown in this dialog, which is where the person is looking when it comes.
 */
function ApproveModal({ proposal: p, onClose, onApproved }: {
  proposal: Proposal;
  onClose: () => void;
  onApproved: () => void;
}) {
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const amount = takes(p);

  return (
    <Modal title={p.gone ? 'Record this request as failed?' : `${headline(p)}?`} onClose={onClose}>
      <ErrorBanner error={error} onDismiss={() => setError(null)} />
      <p className="mt-0 text-sm">
        {p.gone
          ? 'It is no longer there, so nothing will be removed. The request is recorded as failed.'
          : <>
              {amount ? <>This takes {amount}. </> : null}
              The files on disk are untouched. It cannot be undone from here; getting it back means
              indexing it again.
            </>}
      </p>
      <div className="flex justify-end gap-2">
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="danger"
          disabled={busy}
          onClick={async () => {
            setBusy(true);
            setError(null);
            try { await api.approveProposal(p.id); onApproved(); }
            catch (e) { setError(e); setBusy(false); }
          }}
        >
          <Trash2 />
          {busy ? 'Approving…' : p.gone ? 'Record as failed' : 'Approve and remove'}
        </Button>
      </div>
    </Modal>
  );
}
