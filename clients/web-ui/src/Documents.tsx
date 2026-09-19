import { useCallback, useEffect, useRef, useState } from 'react';
import { api, type Corpus, type DocumentText, type LibraryDocument } from './api';
import {
  Badge, Button, CopyButton, Empty, Field, Modal, Notice, Select, SelectItem, Spinner,
  formatBytes, localTime, relativeTime, stateTone,
} from './ui';
import { cn } from 'cn';

/**
 * The document library.
 *
 * The thing this screen has to make obvious, because it is the whole point of the
 * design: a document is stored and extracted ONCE, and each corpus holds its own
 * chunking of it. So every row shows the blob, and underneath it every corpus that
 * attaches it, with that corpus's chunk settings and resulting chunk count side by
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
  const [lastUpload, setLastUpload] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  // Everything the signed-in admin can see is writable; there is no owner to test.
  const writable = corpora;

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
      setLastUpload(null);
      try {
        const result = await api.uploadDocuments(uploadTo, files);
        const deduped = result.stored.filter((s: { deduplicated: boolean }) => s.deduplicated).length;
        // On screen, not in the console. Re-uploading a file that is already stored is
        // the case that looks like nothing happened — same row, same count, no new
        // document — and the explanation was being written somewhere nobody was looking.
        setLastUpload(
          deduped === 0
            ? `${result.stored.length} file${result.stored.length === 1 ? '' : 's'} stored.`
            : `${deduped} of ${result.stored.length} ${deduped === 1 ? 'was' : 'were'} already stored; ` +
              'the existing extraction was reused rather than running again.',
        );
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
    <div className="grid gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="m-0 text-lg">Documents</h1>
        <div className="flex items-center gap-2">
          <label className="text-xs text-muted-foreground" htmlFor="upload-target">Upload into</label>
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
        // `dragleave` also fires as the pointer crosses onto a child, so dragging a file
        // over the zone's own text made it flash on and off. Only a leave that lands
        // outside the zone is a leave.
        onDragLeave={(e) => {
          if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setDragging(false);
        }}
        onDrop={(e) => {
          e.preventDefault();
          setDragging(false);
          void upload(Array.from(e.dataTransfer.files));
        }}
        // A dashed border and a colour change are the whole affordance: nothing else on
        // the page says "you may drop a file here".
        className={cn(
          'card border-dashed p-7 text-center transition-colors',
          dragging && 'border-[var(--accent)] bg-[var(--accent-soft)]',
        )}
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
        <p className="mt-0 mb-2.5 flex items-center justify-center gap-2 font-semibold">
          {busy ? <><Spinner /> Uploading…</> : 'Drop files here'}
        </p>
        <p className="mt-0 mb-3.5 text-sm text-muted-foreground">
          PDF, DOCX, PPTX, EPUB, HTML, Markdown and plain text. Extraction happens once per
          file; chunking happens per corpus.
        </p>
        <Button onClick={() => fileInput.current?.click()} disabled={busy || !uploadTo}>
          Choose files
        </Button>
      </div>

      {lastUpload && (
        <Notice tone="ok">
          {lastUpload}
        </Notice>
      )}

      {loading ? (
        <Empty title="Loading…" />
      ) : docs.length === 0 ? (
        <Empty
          title="No documents yet"
          hint="Upload a PDF or a doc above. The same file can then be attached to several corpora, each chunked its own way."
        />
      ) : (
        <div className="grid gap-3">
          {docs.map((d) => (
            <article key={d.sha256} className="card p-3.5">
              <header className="flex flex-wrap items-baseline gap-2">
                <strong>{d.title || d.originalFileName || d.sha256.slice(0, 12)}</strong>
                {d.title && d.originalFileName && (
                  <span className="mono text-xs text-muted-foreground">{d.originalFileName}</span>
                )}
                <span className="flex-1" />
                <span className="text-xs text-muted-foreground" title={localTime(d.createdUtc)}>
                  {formatBytes(d.sizeBytes)} · {d.extractedChars.toLocaleString()} chars · {relativeTime(d.createdUtc)}
                </span>
              </header>

              {/* Where "why isn't my scanned PDF searchable" gets answered. */}
              {d.emptyReason && (
                <Notice tone="warn" className="mt-2.5">
                  {d.emptyReason}
                </Notice>
              )}

              {/* The comparison that makes the model legible. */}
              <div className="mt-3 grid gap-1">
                {d.attachments.map((a) => (
                  <div
                    key={a.fileId}
                    className="flex flex-wrap items-center gap-2 rounded-md bg-muted px-2 py-1.5 text-sm"
                  >
                    <strong className="min-w-[130px]">{a.corpusName}</strong>
                    <Badge tone={stateTone(a.status)}>{a.status}</Badge>
                    <span className="mono">{a.chunkCount.toLocaleString()} chunks</span>
                    <span className="text-muted-foreground">from {a.chunkSize}/{a.chunkOverlap} {a.boundaryMode}</span>
                    <span className="flex-1" />
                    <Button
                      variant="danger"
                      size="xs"
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

              <div className="mt-3 flex flex-wrap gap-1.5">
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
      <p className="mt-0 text-sm">
        The bytes are already stored and the text already extracted. Attaching re-chunks
        that cached text with the target corpus's settings; nothing is re-uploaded and
        the file is never re-opened.
      </p>

      {available.length === 0 ? (
        <p className="text-sm text-muted-foreground">
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
            <p className="-mt-2 text-sm text-muted-foreground">
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

          <div className="mt-4 flex justify-end gap-2">
            <Button onClick={onClose}>Cancel</Button>
            <Button
              variant="primary"
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
        <p className="flex items-center gap-2"><Spinner /> Loading…</p>
      ) : (
        <>
          <div className="mb-3 text-sm text-muted-foreground">
            <span title={localTime(text.extractedUtc)}>
              {text.extractor} · {text.extractedChars.toLocaleString()} chars · extracted {relativeTime(text.extractedUtc)}
            </span>
          </div>
          {text.emptyReason && (
            <Notice tone="warn" className="mb-2.5">
              {text.emptyReason}
            </Notice>
          )}
          <pre className="mono m-0 max-h-[55vh] overflow-auto rounded-md bg-muted p-3 text-xs break-words whitespace-pre-wrap">
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
