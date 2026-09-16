import { useCallback, useEffect, useRef, useState } from 'react';
import { api, type Corpus, type DocumentText, type LibraryDocument } from './api';
import {
  Badge, Button, CopyButton, Empty, Field, Modal, Select, SelectItem, Spinner,
  formatBytes, localTime, relativeTime, stateTone,
} from './ui';

/**
 * The document library.
 *
 * The thing this screen has to make obvious, because it is the whole point of the
 * design: a document is stored and extracted ONCE, and each corpus holds its own
 * chunking of it. So every row shows the blob, and underneath it every corpus that
 * attaches it — with that corpus's chunk settings and resulting chunk count side by
 * side, where they can be compared.
 */
export function DocumentsView({
  corpora,
  onError,
  onRefresh,
}: {
  corpora: Corpus[];
  onError: (e: unknown) => void;
  onRefresh: () => Promise<void>;
}) {
  const [docs, setDocs] = useState<LibraryDocument[]>([]);
  const [loading, setLoading] = useState(true);
  const [uploadTo, setUploadTo] = useState<string>('');
  const [attaching, setAttaching] = useState<LibraryDocument | null>(null);
  const [inspecting, setInspecting] = useState<LibraryDocument | null>(null);
  const [busy, setBusy] = useState(false);
  const [dragging, setDragging] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);

  const writable = corpora.filter((c) => c.owned);

  const load = useCallback(async () => {
    try {
      setDocs(await api.listDocuments());
    } catch (e) {
      onError(e);
    } finally {
      setLoading(false);
    }
  }, [onError]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!uploadTo && writable.length > 0) setUploadTo(writable[0].name);
  }, [writable, uploadTo]);

  const upload = useCallback(
    async (files: File[]) => {
      if (files.length === 0) return;
      if (!uploadTo) {
        onError(new Error('Choose a corpus to upload into first.'));
        return;
      }
      setBusy(true);
      try {
        const result = await api.uploadDocuments(uploadTo, files);
        const deduped = result.stored.filter((s: { deduplicated: boolean }) => s.deduplicated).length;
        if (deduped > 0) {
          // Worth saying out loud: it looks like nothing happened otherwise.
          // eslint-disable-next-line no-console
          console.info(`${deduped} file(s) were already stored; the existing extraction was reused.`);
        }
        await load();
        await onRefresh();
      } catch (e) {
        onError(e);
      } finally {
        setBusy(false);
      }
    },
    [uploadTo, load, onRefresh, onError],
  );

  return (
    <div style={{ display: 'grid', gap: '1rem' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: '0.5rem' }}>
        <h1 style={{ margin: 0, fontSize: '1.15rem' }}>Documents</h1>
        <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center' }}>
          <label className="dim" style={{ fontSize: '0.8rem' }} htmlFor="upload-target">Upload into</label>
          <Select
            id="upload-target"
            className="w-auto"
            value={uploadTo}
            onValueChange={setUploadTo}
            disabled={writable.length === 0}
            placeholder={writable.length === 0 ? 'No writable corpus' : 'Choose a corpus'}
          >
            {writable.map((c) => (
              <SelectItem key={c.id} value={c.name}>
                {c.name} — {c.chunkSets.length === 1
                  ? `${c.chunkSets[0].chunkSize}/${c.chunkSets[0].chunkOverlap}`
                  : `${c.chunkSets.length} chunk sets`}
              </SelectItem>
            ))}
          </Select>
        </div>
      </div>

      {/* Drop zone */}
      <div
        onDragOver={(e) => { e.preventDefault(); setDragging(true); }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => {
          e.preventDefault();
          setDragging(false);
          void upload(Array.from(e.dataTransfer.files));
        }}
        className="card"
        style={{
          padding: '1.6rem',
          textAlign: 'center',
          borderStyle: 'dashed',
          borderColor: dragging ? 'var(--accent)' : 'var(--border)',
          background: dragging ? 'var(--accent-soft)' : undefined,
          transition: 'background 120ms ease, border-color 120ms ease',
        }}
      >
        <input
          ref={fileInput}
          type="file"
          multiple
          hidden
          onChange={(e) => {
            void upload(Array.from(e.target.files ?? []));
            e.target.value = '';
          }}
        />
        <p style={{ margin: '0 0 0.6rem', fontWeight: 600 }}>
          {busy ? <><Spinner /> Uploading…</> : 'Drop files here'}
        </p>
        <p className="dim" style={{ margin: '0 0 0.9rem', fontSize: '0.82rem' }}>
          PDF, DOCX, PPTX, EPUB, HTML, Markdown and plain text. Extraction happens once per
          file; chunking happens per corpus.
        </p>
        <Button onClick={() => fileInput.current?.click()} disabled={busy || !uploadTo}>
          Choose files
        </Button>
      </div>

      {loading ? (
        <Empty title="Loading…" />
      ) : docs.length === 0 ? (
        <Empty
          title="No documents yet"
          hint="Upload a PDF or a doc above. The same file can then be attached to several corpora, each chunked its own way."
        />
      ) : (
        <div style={{ display: 'grid', gap: '0.7rem' }}>
          {docs.map((d) => (
            <article key={d.sha256} className="card" style={{ padding: '0.9rem' }}>
              <header style={{ display: 'flex', gap: '0.55rem', alignItems: 'baseline', flexWrap: 'wrap' }}>
                <strong>{d.title || d.originalFileName || d.sha256.slice(0, 12)}</strong>
                {d.title && d.originalFileName && (
                  <span className="dim mono" style={{ fontSize: '0.78rem' }}>{d.originalFileName}</span>
                )}
                <span style={{ flex: 1 }} />
                <span className="dim" style={{ fontSize: '0.78rem' }} title={localTime(d.createdUtc)}>
                  {formatBytes(d.sizeBytes)} · {d.extractedChars.toLocaleString()} chars · {relativeTime(d.createdUtc)}
                </span>
              </header>

              {d.emptyReason && (
                <p style={{ margin: '0.5rem 0 0', fontSize: '0.8rem', color: 'var(--warn)' }}>
                  {/* Where "why isn't my scanned PDF searchable" gets answered. */}
                  ⚠ {d.emptyReason}
                </p>
              )}

              {/* The comparison that makes the model legible. */}
              <div style={{ marginTop: '0.7rem', display: 'grid', gap: '0.3rem' }}>
                {d.attachments.map((a) => (
                  <div
                    key={a.fileId}
                    style={{
                      display: 'flex', gap: '0.55rem', alignItems: 'center', flexWrap: 'wrap',
                      padding: '0.35rem 0.55rem', borderRadius: 7, background: 'var(--surface-2)', fontSize: '0.8rem',
                    }}
                  >
                    <strong style={{ minWidth: 130 }}>{a.corpusName}</strong>
                    <Badge tone={stateTone(a.status)}>{a.status}</Badge>
                    <span className="mono">{a.chunkCount} chunks</span>
                    <span className="dim">from {a.chunkSize}/{a.chunkOverlap} {a.boundaryMode}</span>
                    <span style={{ flex: 1 }} />
                    <Button variant="danger"
                      style={{ padding: '0.1rem 0.45rem', fontSize: '0.72rem' }}
                      onClick={async () => {
                        try {
                          await api.detachDocument(a.corpusName, a.fileId);
                          await load();
                          await onRefresh();
                        } catch (e) { onError(e); }
                      }}
                    >
                      Detach
                    </Button>
                  </div>
                ))}
              </div>

              <div style={{ display: 'flex', gap: '0.4rem', marginTop: '0.7rem', flexWrap: 'wrap' }}>
                <Button onClick={() => setAttaching(d)} disabled={writable.length === 0}>
                  Attach to another corpus…
                </Button>
                <Button onClick={() => setInspecting(d)}>View extracted text</Button>
                <CopyButton text={d.sha256} label="Copy hash" />
              </div>
            </article>
          ))}
        </div>
      )}

      {attaching && (
        <AttachModal
          document={attaching}
          corpora={writable}
          onClose={() => setAttaching(null)}
          onAttached={async () => { setAttaching(null); await load(); await onRefresh(); }}
          onError={onError}
        />
      )}

      {inspecting && <ExtractedTextModal document={inspecting} onClose={() => setInspecting(null)} onError={onError} />}
    </div>
  );
}

function AttachModal({
  document: doc,
  corpora,
  onClose,
  onAttached,
  onError,
}: {
  document: LibraryDocument;
  corpora: Corpus[];
  onClose: () => void;
  onAttached: () => Promise<void>;
  onError: (e: unknown) => void;
}) {
  const attachedTo = new Set(doc.attachments.map((a) => a.corpusId));
  const available = corpora.filter((c) => !attachedTo.has(c.id));
  const [target, setTarget] = useState(available[0]?.name ?? '');
  const [busy, setBusy] = useState(false);

  const chosen = corpora.find((c) => c.name === target);

  return (
    <Modal title="Attach to another corpus" onClose={onClose}>
      <p style={{ marginTop: 0, fontSize: '0.86rem' }}>
        The bytes are already stored and the text already extracted. Attaching re-chunks
        that cached text with the target corpus's settings — nothing is re-uploaded and
        the file is never re-opened.
      </p>

      {available.length === 0 ? (
        <p className="dim" style={{ fontSize: '0.85rem' }}>
          This document is already attached to every corpus you can write to.
        </p>
      ) : (
        <>
          <Field label="Corpus">
            <Select value={target} onValueChange={setTarget} placeholder="Choose a corpus">
              {available.map((c) => (
                <SelectItem key={c.id} value={c.name}>{c.name}</SelectItem>
              ))}
            </Select>
          </Field>

          {chosen && (
            <p className="dim" style={{ fontSize: '0.8rem', marginTop: '-0.45rem' }}>
              {/* An upload is queued into EVERY set, so naming only the default would
                  understate what is about to happen. */}
              Will be chunked {chosen.chunkSets.length === 1 ? 'as' : 'by each of'}{' '}
              {chosen.chunkSets.map((s, i) => (
                <span key={s.id}>
                  {i > 0 && ', '}
                  <strong>{s.name}</strong> ({s.chunkSize}/{s.chunkOverlap}, {s.boundaryMode})
                </span>
              ))}
              .
            </p>
          )}

          <div style={{ display: 'flex', gap: '0.5rem', justifyContent: 'flex-end', marginTop: '1rem' }}>
            <Button onClick={onClose}>Cancel</Button>
            <Button variant="primary"
              disabled={!target || busy}
              onClick={async () => {
                setBusy(true);
                try {
                  await api.attachDocument(target, doc.sha256, doc.originalFileName ?? undefined);
                  await onAttached();
                } catch (e) { onError(e); setBusy(false); }
              }}
            >
              {busy ? <Spinner /> : null} Attach
            </Button>
          </div>
        </>
      )}
    </Modal>
  );
}

function ExtractedTextModal({
  document: doc,
  onClose,
  onError,
}: {
  document: LibraryDocument;
  onClose: () => void;
  onError: (e: unknown) => void;
}) {
  const [text, setText] = useState<DocumentText | null>(null);

  useEffect(() => {
    api.documentText(doc.sha256).then(setText).catch(onError);
  }, [doc.sha256, onError]);

  return (
    <Modal title={doc.originalFileName ?? 'Extracted text'} onClose={onClose} width={860}>
      {/* "What did the extractor actually see?" is the first question when results are
          wrong, and it should not require a database client to answer. */}
      {!text ? (
        <p><Spinner /> Loading…</p>
      ) : (
        <>
          <div className="dim" style={{ fontSize: '0.8rem', marginBottom: '0.7rem' }}>
            <span title={localTime(text.extractedUtc)}>
              {text.extractor} · {text.extractedChars.toLocaleString()} chars · extracted {relativeTime(text.extractedUtc)}
            </span>
          </div>
          {text.emptyReason && (
            <p style={{ color: 'var(--warn)', fontSize: '0.85rem' }}>⚠ {text.emptyReason}</p>
          )}
          <pre
            className="mono"
            style={{
              background: 'var(--surface-2)', padding: '0.7rem', borderRadius: 7,
              fontSize: '0.75rem', maxHeight: '55vh', overflow: 'auto',
              whiteSpace: 'pre-wrap', wordBreak: 'break-word', margin: 0,
            }}
          >
            {text.preview || '(nothing was extracted)'}
          </pre>
        </>
      )}
    </Modal>
  );
}

/**
 * Chunk settings, editable. Changing any of them re-chunks the whole corpus, so the
 * dialog says so plainly and shows what it will cost.
 */
