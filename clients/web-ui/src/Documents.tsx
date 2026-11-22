import { useCallback, useEffect, useRef, useState } from 'react';
import {
  api, type Corpus, type DocumentText, type LibraryAttachment, type LibraryDocument, type UploadFailure,
} from './api';
import {
  Badge, Button, CopyButton, Empty, ErrorBanner, Field, Modal, Notice, Select, SelectItem, Spinner,
  formatBytes, localTime, relativeTime, stateTone,
} from './ui';
import { cn } from 'cn';
import { count } from './lib/units';

/** How often the list is read again while an attachment is still pending. */
const PENDING_REFRESH_MS = 3000;

const TARGET_KEY = 'dexicon.uploadTarget';

// The corpus last uploaded into, in this browser. A convenience: without it, a corpus is chosen again each time.
function rememberedTarget(): string | null {
  try {
    return localStorage.getItem(TARGET_KEY);
  } catch {
    return null;
  }
}

function rememberTarget(name: string) {
  try {
    localStorage.setItem(TARGET_KEY, name);
  } catch {
    // Storage may be blocked; the choice is then not remembered.
  }
}

const sameFile = (a: File, b: File) => a.name === b.name && a.size === b.size && a.lastModified === b.lastModified;

/**
 * The document library.
 *
 * Files are staged first: a drop or a choice adds them to a list, and the corpus they go into is chosen beside that list, so
 * the drop is never refused for lack of a target and nothing is uploaded until Upload is pressed.
 *
 * What this screen has to make obvious, because it is the point of the design: a document
 * is stored and extracted once, and each corpus holds its own chunking of it. So every row shows the blob, and underneath it every corpus that
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
  const [staged, setStaged] = useState<File[]>([]);
  const [attaching, setAttaching] = useState<LibraryDocument | null>(null);
  const [inspecting, setInspecting] = useState<LibraryDocument | null>(null);
  const [detaching, setDetaching] = useState<{ document: LibraryDocument; attachment: LibraryAttachment } | null>(null);
  const [busy, setBusy] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [lastUpload, setLastUpload] = useState<string | null>(null);
  const [notStored, setNotStored] = useState<UploadFailure[]>([]);
  // `file` is null for a failure of the request as a whole (a body over the bound, one that cannot
  // be read, a connection that closed). A file's own name says nothing about which it is.
  const refusedFiles = notStored.filter((f) => f.file != null);
  const requestFailures = notStored.filter((f) => f.file == null);
  const fileInput = useRef<HTMLInputElement>(null);
  // Set the moment an upload starts, before `busy` has rendered, so a second drop in the same
  // tick is refused too. The drop zone stays live while an upload runs; only the button is disabled.
  const uploading = useRef(false);

  // Everything the signed-in admin can see is writable; there is no owner to test.
  const writable = corpora;
  const targetName = writable.find((c) => c.name === uploadTo)?.name;
  const targetSets = writable.find((c) => c.name === uploadTo)?.chunkSets;

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

  // An attachment reads "pending" from the moment it is attached until its corpus's pass has
  // chunked it, which takes seconds. The list was read once on entry, so the row kept reading
  // "pending" until the page was left and entered again. While any attachment is pending the
  // list is read again every few seconds. A read that fails is tried again on the next tick and
  // raises no error of its own: the first read reports an unreachable server.
  const anyPending = docs.some((d) => d.attachments.some((a) => a.status === 'pending'));
  useEffect(() => {
    if (!anyPending) return;
    let reading = false;
    const timer = setInterval(async () => {
      if (reading) return;
      reading = true;
      try {
        setDocs(await api.listDocuments());
      } catch {
        // The next tick tries again.
      } finally {
        reading = false;
      }
    }, PENDING_REFRESH_MS);
    return () => clearInterval(timer);
  }, [anyPending]);

  // The corpus is the one last uploaded into, or the only one there is. Otherwise it is left for the person to choose, and a
  // corpus that has gone is dropped.
  useEffect(() => {
    if (uploadTo && !writable.some((c) => c.name === uploadTo)) {
      setUploadTo('');
      return;
    }
    if (uploadTo) return;
    const remembered = rememberedTarget();
    const pick = writable.find((c) => c.name === remembered) ?? (writable.length === 1 ? writable[0] : undefined);
    if (pick) setUploadTo(pick.name);
  }, [writable, uploadTo]);

  // Adds files to the list. A file already in it (same name, size and date) is not added twice.
  const stage = useCallback((files: File[]) => {
    if (uploading.current) return;
    setStaged((current) => {
      const next = [...current];
      for (const f of files) if (!next.some((c) => sameFile(c, f))) next.push(f);
      return next;
    });
  }, []);

  const upload = useCallback(
    async () => {
      const files = staged;
      if (files.length === 0) return;
      // A second sequence of requests would overwrite the first one's result on screen.
      if (uploading.current) return;
      if (!uploadTo) {
        onError(new Error('Choose a corpus to add the files to first.'));
        return;
      }
      uploading.current = true;
      setBusy(true);
      setLastUpload(null);
      setNotStored([]);
      try {
        const result = await api.uploadDocuments(uploadTo, files);
        rememberTarget(uploadTo);
        // The files are with the server now. Those it refused are named below, and are dropped on the zone again.
        setStaged([]);
        // A batch is accepted when any file is stored. The files the server refused (over the
        // size cap, empty, a name with a control character or over 260 characters, a rename onto
        // another document's name, or an extraction that timed out or failed on the server) come
        // back in `failed` beside the stored ones, and are the only place the reason is said.
        setNotStored(result.failed);
        setLastUpload(uploadSummary(result.stored));
        await load();
        await onRefresh();
      } catch (e) {
        onError(e);
      } finally {
        uploading.current = false;
        setBusy(false);
      }
    },
    [staged, uploadTo, load, onRefresh, onError],
  );

  return (
    <div className="grid gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="m-0 text-lg">Documents</h1>
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
          stage(Array.from(e.dataTransfer.files));
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
            stage(Array.from(e.target.files ?? []));
            e.target.value = '';
          }}
        />
        <p className="mt-0 mb-2.5 flex items-center justify-center gap-2 font-semibold">
          {busy ? <><Spinner /> Uploading…</> : 'Drop files here'}
        </p>
        <p className="mt-0 mb-3.5 text-sm text-muted-foreground">
          PDF, DOCX, PPTX, EPUB, HTML, Markdown and plain text. Each file's text is extracted once and
          chunked separately for each corpus.
        </p>
        <Button onClick={() => fileInput.current?.click()} disabled={busy}>
          Choose files
        </Button>
      </div>

      {staged.length > 0 && (
        <section aria-label="Files to upload" className="card grid gap-3 p-3.5">
          <h2 className="m-0 text-base">{count(staged.length, 'file')} to upload</h2>
          <ul className="m-0 grid list-none gap-1 p-0 text-sm">
            {staged.map((f) => (
              <li key={`${f.name}-${f.size}-${f.lastModified}`} className="flex flex-wrap items-center gap-2 rounded-md bg-muted px-2 py-1.5">
                <span className="[overflow-wrap:anywhere]">{f.name}</span>
                <span className="text-xs text-muted-foreground">{formatBytes(f.size)}</span>
                <span className="flex-1" />
                <Button
                  size="xs"
                  disabled={busy}
                  aria-label={`Remove ${f.name}`}
                  onClick={() => setStaged((current) => current.filter((c) => c !== f))}
                >
                  Remove
                </Button>
              </li>
            ))}
          </ul>

          <div className="flex flex-wrap items-center gap-2">
            <label className="text-sm" htmlFor="upload-target">Add to</label>
            <Select
              id="upload-target"
              className="w-auto"
              value={uploadTo}
              onValueChange={setUploadTo}
              disabled={writable.length === 0 || busy}
              placeholder={writable.length === 0 ? 'No corpus yet' : 'Choose a corpus'}
            >
              {writable.map((c) => (
                <SelectItem key={c.id} value={c.name}>{c.name}</SelectItem>
              ))}
            </Select>
          </div>
          {targetSets && (
            <p className="m-0 text-xs text-muted-foreground">
              Chunked as {targetSets.map((t) => `${targetName}:${t.name} (${t.chunkSize.toLocaleString()} tokens / ${t.chunkOverlap.toLocaleString()} overlap)`).join(', ')}.
            </p>
          )}

          <div className="flex justify-end gap-2">
            <Button disabled={busy} onClick={() => setStaged([])}>Clear</Button>
            <Button variant="primary" disabled={busy || !uploadTo} onClick={() => void upload()}>
              {busy ? <Spinner /> : null}
              Upload {count(staged.length, 'file')}
            </Button>
          </div>
        </section>
      )}

      {lastUpload && (
        <Notice tone="ok">
          {lastUpload}
        </Notice>
      )}

      {notStored.length > 0 && (
        <Notice tone="warn">
          {/* The files counted are the ones named. A failure of the whole request is not a file
              and follows the list as a sentence of its own. */}
          {refusedFiles.length > 0 && (
            <>
              <p className="m-0 font-semibold">{count(refusedFiles.length, 'file')} not stored:</p>
              <ul className="m-0 mt-1 list-disc pl-5">
                {refusedFiles.map((f, i) => (
                  <li key={`${f.file}-${i}`}>{f.error.includes(`'${f.file}'`) ? f.error : `${f.file}: ${f.error}`}</li>
                ))}
              </ul>
            </>
          )}
          {requestFailures.map((f, i) => (
            <p key={i} className={cn('m-0', refusedFiles.length > 0 && 'mt-1')}>{f.error}</p>
          ))}
        </Notice>
      )}

      {loading ? (
        <Empty title="Loading…" />
      ) : docs.length === 0 ? (
        <Empty
          title="No documents yet"
          hint="Upload a document above. A document can be attached to several corpora, and each corpus chunks it with its own settings."
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
                    <span className="mono">{count(a.chunkCount, 'chunk')}</span>
                    {/* The corpus's name for the document when it is not the name it was first uploaded under:
                        identical bytes uploaded again under another name rename the attachment. */}
                    {a.fileName !== d.originalFileName && <span className="mono">as {a.fileName}</span>}
                    <span className="text-muted-foreground">
                      at {a.chunkSize.toLocaleString()} tokens, {a.chunkOverlap.toLocaleString()} overlap, {a.boundaryMode}
                    </span>
                    <span className="flex-1" />
                    <Button
                      variant="danger"
                      size="xs"
                      onClick={() => setDetaching({ document: d, attachment: a })}
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
        />
      )}

      {inspecting && <ExtractedTextModal document={inspecting} onClose={() => setInspecting(null)} />}

      {detaching && (
        <DetachModal
          document={detaching.document}
          attachment={detaching.attachment}
          onClose={() => setDetaching(null)}
          onDetached={async () => { setDetaching(null); await load(); await onRefresh(); }}
        />
      )}
    </div>
  );
}

/**
 * Detaching deletes the document's chunks in that corpus, and the document itself when no other corpus holds it:
 * its extracted text and its bytes go too, and getting it back means uploading it again. So it asks first, and
 * says which of the two it is about to do.
 */
function DetachModal({
  document: doc,
  attachment,
  onClose,
  onDetached,
}: {
  document: LibraryDocument;
  attachment: LibraryAttachment;
  onClose: () => void;
  onDetached: () => Promise<void>;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const others = doc.attachments.filter((a) => a.fileId !== attachment.fileId);

  return (
    <Modal title={`Detach ${attachment.fileName} from ${attachment.corpusName}?`} onClose={onClose}>
      <ErrorBanner error={error} onDismiss={() => setError(null)} />
      <p className="mt-0 text-sm">
        {others.length === 0
          ? 'No other corpus holds this document, so it is deleted, with its extracted text, its stored file '
            + 'and its chunks. Upload it again to restore it.'
          : `${others.map((o) => o.corpusName).join(', ')} ${others.length === 1 ? 'keeps' : 'keep'} it. `
            + `Its chunks in ${attachment.corpusName} are deleted.`}
      </p>
      <div className="flex gap-2 justify-end">
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="danger"
          disabled={busy}
          onClick={async () => {
            setBusy(true);
            setError(null);
            try { await api.detachDocument(attachment.corpusName, attachment.fileId); await onDetached(); }
            catch (e) { setError(e); setBusy(false); }
          }}
        >
          {busy ? 'Detaching…' : 'Detach'}
        </Button>
      </div>
    </Modal>
  );
}

/** The server's extractor class, as the format it reads. An unknown name is shown as sent. */
export function extractorLabel(extractor: string): string {
  const labels: Record<string, string> = {
    PdfTextExtractor: 'PDF',
    DocxTextExtractor: 'DOCX',
    PptxTextExtractor: 'PPTX',
    EpubTextExtractor: 'EPUB',
    HtmlTextExtractor: 'HTML',
    ClaudeTranscriptTextExtractor: 'Claude Code transcript',
    PlainText: 'plain text',
  };
  return labels[extractor] ?? extractor;
}

/**
 * What an upload did, in sentences. Identical bytes are one document per corpus, so two names for them
 * leave one attachment under the later name (DocumentService.AttachHeldAsync); the server answers with the
 * same file id for both, which is how they are found here.
 */
export function uploadSummary(stored: { fileName: string; fileId: string; deduplicated: boolean }[]): string {
  const sentences = [`${count(stored.length, 'file')} stored.`];
  const reused = stored.filter((s) => s.deduplicated).length;
  if (reused > 0)
    sentences.push(
      `${reused} ${reused === 1 ? 'matches a document' : 'match documents'} already stored, so ` +
        `${reused === 1 ? 'its' : 'their'} text was not extracted again.`,
    );
  const namesByFile = new Map<string, string[]>();
  for (const s of stored) {
    if (!s.fileId) continue;
    const names = namesByFile.get(s.fileId) ?? [];
    if (!names.includes(s.fileName)) names.push(s.fileName);
    namesByFile.set(s.fileId, names);
  }
  for (const names of namesByFile.values()) {
    if (names.length < 2) continue;
    const last = names[names.length - 1];
    sentences.push(
      `${names.slice(0, -1).join(', ')} and ${last} have the same content, so the corpus holds one document, named ${last}.`,
    );
  }
  return sentences.join(' ');
}

function AttachModal({
  document: doc,
  corpora,
  onClose,
  onAttached,
}: {
  document: LibraryDocument;
  corpora: Corpus[];
  onClose: () => void;
  onAttached: () => Promise<void>;
}) {
  const attachedTo = new Set(doc.attachments.map((a) => a.corpusId));
  const available = corpora.filter((c) => !attachedTo.has(c.id));
  // Chosen for the user only when there is one: preselecting the first of several put a real
  // corpus one click from holding a document meant for another.
  const [target, setTarget] = useState(available.length === 1 ? available[0].name : '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const chosen = corpora.find((c) => c.name === target);

  return (
    <Modal title="Attach to another corpus" onClose={onClose}>
      <ErrorBanner error={error} onDismiss={() => setError(null)} />
      <p className="mt-0 text-sm">
        The file is already stored and its text extracted. Attaching splits that text into chunks
        with the chosen corpus's settings. Nothing is uploaded again.
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
                  <strong>{s.name}</strong> ({s.chunkSize.toLocaleString()} tokens / {s.chunkOverlap.toLocaleString()} overlap, {s.boundaryMode})
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
                setError(null);
                try {
                  await api.attachDocument(target, doc.sha256, doc.originalFileName ?? undefined);
                  await onAttached();
                } catch (e) { setError(e); setBusy(false); }
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
}: {
  document: LibraryDocument;
  onClose: () => void;
}) {
  const [text, setText] = useState<DocumentText | null>(null);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    api.documentText(doc.sha256).then(setText).catch(setError);
  }, [doc.sha256]);

  return (
    <Modal title={doc.originalFileName ?? 'Extracted text'} onClose={onClose} width={860}>
      {/* The load is the only request here, so its failure stays until the dialog closes. */}
      <ErrorBanner error={error} onDismiss={text ? () => setError(null) : undefined} />
      {/* "What did the extractor actually see?" is the first question when results are
          wrong, and it should not require a database client to answer. */}
      {!text ? (
        // A load that failed says so above, rather than loading for ever.
        error == null && <p className="flex items-center gap-2"><Spinner /> Loading…</p>
      ) : (
        <>
          <div className="mb-3 text-sm text-muted-foreground">
            <span title={localTime(text.extractedUtc)}>
              Read as {extractorLabel(text.extractor)} · {text.extractedChars.toLocaleString()} chars · extracted {relativeTime(text.extractedUtc)}
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
