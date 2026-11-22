import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DocumentsView } from './Documents';
import { ApiError, type Corpus, type LibraryDocument } from './api';

/**
 * The document library.
 *
 * The thing this screen exists to make obvious is the model: a document is stored and
 * extracted ONCE, and each corpus holds its own chunking of it. If that is not on the
 * screen, people reasonably assume uploading the same PDF twice costs twice as much and
 * that attaching it somewhere else re-reads the file.
 *
 * It is also the only screen where being wrong loses something: uploading into the corpus
 * you did not mean.
 */
const listDocuments = vi.fn();
const uploadDocuments = vi.fn();
const attachDocument = vi.fn();
const documentText = vi.fn();
const detachDocument = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: {
    listDocuments: (...a: unknown[]) => listDocuments(...a),
    uploadDocuments: (...a: unknown[]) => uploadDocuments(...a),
    attachDocument: (...a: unknown[]) => attachDocument(...a),
    detachDocument: (...a: unknown[]) => detachDocument(...a),
    documentText: (...a: unknown[]) => documentText(...a),
  },
}));

function corpus(name: string, over: Partial<Corpus> = {}): Corpus {
  return {
    id: `id-${name}`,
    name,
    description: null,
    state: 'ready',
    createdUtc: new Date().toISOString(),
    lastIndexedUtc: null,
    sourceCount: 0,
    fileCount: 0,
    chunkCount: 0,
    skippedCount: 0,
    pendingCount: 0,
    failedCount: 0,
    sources: [],
    chunkSets: [
      {
        id: `set-${name}`,
        name: 'default',
        description: null,
        embeddingProvider: 'ollama',
        embeddingModel: 'nomic-embed-text',
        embeddingDimensions: 768,
        collectionName: 'c',
        chunkSize: 768,
        chunkOverlap: 100,
        boundaryMode: 'language-aware',
        customBoundaryPattern: null,
        unitAware: false,
        sentenceAware: false,
        headingContext: false,
        isDefault: true,
        state: 'ready',
        fileCount: 0,
        chunkCount: 0,
        pendingCount: 0,
        failedCount: 0,
        createdUtc: new Date().toISOString(),
        lastIndexedUtc: null,
      },
    ],
    ...over,
  };
}

const doc: LibraryDocument = {
  sha256: 'abc123def456abc123def456',
  title: 'Moby-Dick',
  originalFileName: 'moby-dick.epub',
  mediaType: 'application/epub+zip',
  sizeBytes: 1_200_000,
  extractedChars: 1_100_000,
  extractor: 'epub',
  emptyReason: null,
  createdUtc: new Date().toISOString(),
  extractedUtc: new Date().toISOString(),
  attachments: [
    {
      fileId: 'f1',
      corpusId: 'id-library',
      corpusName: 'library',
      fileName: 'moby-dick.epub',
      status: 'indexed',
      chunkCount: 980,
      chunkSize: 768,
      chunkOverlap: 100,
      boundaryMode: 'language-aware',
    },
    {
      fileId: 'f2',
      corpusId: 'id-fine',
      corpusName: 'fine',
      fileName: 'moby-dick.epub',
      status: 'indexed',
      chunkCount: 2940,
      chunkSize: 256,
      chunkOverlap: 40,
      boundaryMode: 'blank-line',
    },
  ],
} as unknown as LibraryDocument;

const noop = { onRefresh: async () => {}, onError: vi.fn() };

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  listDocuments.mockResolvedValue([doc]);
});

describe('the document library', () => {
  it('shows each corpus that holds the same bytes, with its own chunking', async () => {
    // The whole point of the design, on the screen: one stored document, two chunkings of
    // it, side by side where they can be compared.
    render(<DocumentsView corpora={[corpus('library'), corpus('fine')]} {...noop} />);

    const row = await screen.findByText('library');
    const card = row.closest('article')!;

    expect(within(card).getByText('980 chunks')).toBeInTheDocument();
    expect(within(card).getByText(/at 768 tokens, 100 overlap, language-aware/)).toBeInTheDocument();
    // Formatted the way the component formats it. The literal used to be hard-coded with
    // a `.replace(',', ',')` beside it, which replaced a comma with a comma and did
    // nothing; the separator it was presumably meant to normalise is locale-dependent,
    // so under de-DE or fr-FR this asserted on a string the component never renders.
    expect(within(card).getByText(`${(2940).toLocaleString()} chunks`)).toBeInTheDocument();
    expect(within(card).getByText(/at 256 tokens, 40 overlap, blank-line/)).toBeInTheDocument();
  });

  it('takes dropped files before a corpus is chosen, and will not upload until one is', async () => {
    // Upload goes into ONE corpus. A button that is live before the target is picked is an invitation to put a
    // file somewhere at random, but a drop is not refused for the lack of one: the files wait in a list.
    const user = userEvent.setup();
    const { container } = render(<DocumentsView corpora={[corpus('library'), corpus('fine')]} {...noop} />);
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;

    await user.upload(input, new File(['x'], 'notes.txt'));

    expect(await screen.findByText('1 file to upload')).toBeInTheDocument();
    expect(screen.getByLabelText('Add to')).toHaveTextContent(/choose a corpus/i);
    expect(screen.getByRole('button', { name: 'Upload 1 file' })).toBeDisabled();
    expect(uploadDocuments).not.toHaveBeenCalled();

    await user.click(screen.getByLabelText('Add to'));
    await user.click(await screen.findByRole('option', { name: 'fine' }));

    expect(screen.getByRole('button', { name: 'Upload 1 file' })).toBeEnabled();
  });

  it('says how the files will be chunked in the corpus chosen, with the units', async () => {
    // It read "books — 1843/258": two numbers and no way to know what they measure.
    const user = userEvent.setup();
    const { container } = render(<DocumentsView corpora={[corpus('library')]} {...noop} />);
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;

    await user.upload(input, new File(['x'], 'notes.txt'));

    expect(await screen.findByText('Chunked as library:default (768 tokens / 100 overlap).')).toBeInTheDocument();
  });

  it('names every chunk set of the corpus, since a file is chunked for each', async () => {
    const two = corpus('library');
    two.chunkSets = [two.chunkSets[0], { ...two.chunkSets[0], id: 'set-2', name: 'fine', chunkSize: 256, chunkOverlap: 40, isDefault: false }];
    const user = userEvent.setup();
    const { container } = render(<DocumentsView corpora={[two]} {...noop} />);

    await user.upload(container.querySelector('input[type="file"]') as HTMLInputElement, new File(['x'], 'notes.txt'));

    expect(await screen.findByText(
      'Chunked as library:default (768 tokens / 100 overlap), library:fine (256 tokens / 40 overlap).',
    )).toBeInTheDocument();
  });

  it('says there is nowhere to put the files rather than offering an empty picker', async () => {
    const user = userEvent.setup();
    const { container } = render(<DocumentsView corpora={[]} {...noop} />);

    await user.upload(container.querySelector('input[type="file"]') as HTMLInputElement, new File(['x'], 'notes.txt'));

    expect(await screen.findByLabelText('Add to')).toHaveTextContent(/no corpus yet/i);
    expect(screen.getByRole('button', { name: 'Upload 1 file' })).toBeDisabled();
  });

  it('does not offer to attach a document to a corpus that already has it', async () => {
    // Attaching twice is not a second copy, it is an error. The list simply excludes them.
    const user = userEvent.setup();
    render(
      <DocumentsView
        corpora={[corpus('library'), corpus('fine'), corpus('archive')]}
        {...noop}
      />,
    );

    await user.click(await screen.findByRole('button', { name: /attach to another corpus/i }));
    const dialog = await screen.findByRole('dialog');

    await user.click(within(dialog).getByLabelText('Corpus'));
    const options = (await screen.findAllByRole('option')).map((o) => o.textContent);

    expect(options).toEqual(['archive']);
  });

  it('says how the attached copy will be chunked before it is attached', async () => {
    // "Nothing is re-uploaded" is the reassurance; "chunked as default (768 tokens / 100 overlap)" is the
    // consequence. Both belong on screen before the button is pressed.
    const user = userEvent.setup();
    render(<DocumentsView corpora={[corpus('library'), corpus('archive')]} {...noop} />);

    await user.click(await screen.findByRole('button', { name: /attach to another corpus/i }));
    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).getByText(/Nothing is uploaded again/i)).toBeInTheDocument();
    await waitFor(() =>
      expect(within(dialog).getByText(/768 tokens \/ 100 overlap/)).toBeInTheDocument(),
    );
  });

  it('explains an empty extraction where the question gets asked', async () => {
    // "Why isn't my scanned PDF searchable", answered on the document rather than in a log.
    listDocuments.mockResolvedValue([
      { ...doc, emptyReason: 'No text layer; this looks like a scan.' },
    ]);

    render(<DocumentsView corpora={[corpus('library')]} {...noop} />);

    expect(await screen.findByText(/No text layer/)).toBeInTheDocument();
  });
  /**
   * The load is the only request this dialog makes. Its failure went to the page, behind
   * the dialog, which went on saying "Loading…" for ever; and dismissing the failure must
   * not bring that back.
   */
  it('says in the dialog when the extracted text cannot be read', async () => {
    documentText.mockRejectedValue(new ApiError(404, 'No stored text', 'Nothing is stored for this document.'));
    const user = userEvent.setup();
    render(<DocumentsView corpora={[corpus('library')]} {...noop} />);

    await user.click(await screen.findByRole('button', { name: 'View extracted text' }));
    const dialog = await screen.findByRole('dialog');

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(/Nothing is stored/);
    expect(within(dialog).queryByText(/Loading…/)).not.toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Dismiss error' })).not.toBeInTheDocument();
    expect(noop.onError).not.toHaveBeenCalled();
  });
});

describe('uploading a batch', () => {
  const stored = (fileName: string) => ({ fileName, deduplicated: false });

  async function renderReady() {
    const user = userEvent.setup();
    const { container } = render(<DocumentsView corpora={[corpus('library')]} {...noop} />);
    await waitFor(() => expect(screen.getByRole('button', { name: /choose files/i })).toBeEnabled());
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    // The files are added to the list, then sent by the button.
    const addAndUpload = async (files: File | File[]) => {
      await user.upload(input, files);
      await user.click(await screen.findByRole('button', { name: /^Upload \d+ files?$/ }));
    };
    return { user, input, addAndUpload };
  }

  /**
   * A batch is accepted when any file is stored, and the files the server refused come back in
   * `failed`. The screen said "1 file stored." and nothing else, so a file over the size cap
   * vanished from a drop of two with no reason anywhere.
   */
  it('names each file the server refused beside the count it stored', async () => {
    uploadDocuments.mockResolvedValue({
      corpus: 'library',
      stored: [stored('notes.txt')],
      failed: [
        { file: 'huge.iso', error: 'huge.iso is over the 200 MB limit.' },
        { file: null, error: 'The request is over the 2.0 GB bound.' },
      ],
      job: {},
    });
    const { addAndUpload } = await renderReady();

    await addAndUpload([new File(['x'], 'notes.txt'), new File(['x'], 'huge.iso')]);

    expect(await screen.findByText('1 file stored.')).toBeInTheDocument();
    // Two entries came back, one of them the request as a whole: one file was refused.
    expect(screen.getByText('1 file not stored:')).toBeInTheDocument();
    expect(screen.queryByText(/2 files not stored/)).not.toBeInTheDocument();
    expect(screen.getByText('huge.iso: huge.iso is over the 200 MB limit.')).toBeInTheDocument();
    // A failure of the request as a whole is not attributed to a file.
    expect(screen.getByText('The request is over the 2.0 GB bound.')).toBeInTheDocument();
    expect(screen.queryByText(/null/)).not.toBeInTheDocument();
  });

  it('counts a refused file whose name looks like a marker as a file', async () => {
    // Any name is a legal file name. Telling the request's failure from a file's by its name sent a
    // refused file called "(request)" to the sentence list, uncounted and unnamed.
    uploadDocuments.mockResolvedValue({
      corpus: 'library',
      stored: [stored('notes.txt')],
      failed: [{ file: '(request)', error: "'(request)' is empty." }],
      job: {},
    });
    const { addAndUpload } = await renderReady();

    await addAndUpload([new File(['x'], 'notes.txt'), new File([''], '(request)')]);

    expect(await screen.findByText('1 file not stored:')).toBeInTheDocument();
    expect(screen.getByText("(request): '(request)' is empty.")).toBeInTheDocument();
  });

  it('shows a failure of the whole request without counting a file', async () => {
    uploadDocuments.mockResolvedValue({
      corpus: 'library',
      stored: [stored('notes.txt')],
      failed: [{ file: null, error: 'The request is over the 2.0 GB bound.' }],
      job: {},
    });
    const { addAndUpload } = await renderReady();

    await addAndUpload(new File(['x'], 'notes.txt'));

    expect(await screen.findByText('The request is over the 2.0 GB bound.')).toBeInTheDocument();
    expect(screen.queryByText(/files? not stored/)).not.toBeInTheDocument();
  });

  it('lists every failure of a whole request when a drop was sent as several', async () => {
    // A drop of more than ten files is several requests, and the merged answer can carry one
    // request-level failure from each.
    uploadDocuments.mockResolvedValue({
      corpus: 'library',
      stored: [stored('a.txt'), stored('b.txt')],
      failed: [
        { file: 'c.txt', error: "'c.txt' is empty." },
        { file: null, error: 'The first request was refused.' },
        { file: null, error: 'The second request was refused.' },
      ],
      job: {},
    });
    const { addAndUpload } = await renderReady();

    await addAndUpload([new File(['x'], 'a.txt'), new File(['x'], 'b.txt')]);

    expect(await screen.findByText('2 files stored.')).toBeInTheDocument();
    expect(screen.getByText('1 file not stored:')).toBeInTheDocument();
    expect(screen.getByText('The first request was refused.')).toBeInTheDocument();
    expect(screen.getByText('The second request was refused.')).toBeInTheDocument();
  });

  it('counts and names the files a stopped drop did not send', async () => {
    // After a failed request the remaining files are not sent. Each is listed by name under the
    // count, beside the sentence for the request that failed.
    uploadDocuments.mockResolvedValue({
      corpus: 'library',
      stored: [stored('a.txt'), stored('b.txt')],
      failed: [
        { file: null, error: 'Service Unavailable: The catalogue is busy.' },
        { file: 'c.txt', error: 'Not sent: an earlier request failed.' },
        { file: 'd.txt', error: 'Not sent: an earlier request failed.' },
      ],
      job: {},
    });
    const { addAndUpload } = await renderReady();

    await addAndUpload([new File(['x'], 'a.txt'), new File(['x'], 'b.txt')]);

    expect(await screen.findByText('2 files stored.')).toBeInTheDocument();
    expect(screen.getByText('2 files not stored:')).toBeInTheDocument();
    expect(screen.getByText('c.txt: Not sent: an earlier request failed.')).toBeInTheDocument();
    expect(screen.getByText('d.txt: Not sent: an earlier request failed.')).toBeInTheDocument();
    expect(screen.getByText('Service Unavailable: The catalogue is busy.')).toBeInTheDocument();
  });

  it('ignores a drop made while an upload is still running', async () => {
    // A second sequence of requests would overwrite the first one's result on screen, and files added now would be
    // cleared with the first batch.
    let finish: (v: unknown) => void = () => {};
    uploadDocuments.mockReturnValueOnce(new Promise((resolve) => { finish = resolve; }));
    const { user, input } = await renderReady();
    const zone = screen.getByText('Drop files here').parentElement as HTMLElement;

    fireEvent.drop(zone, { dataTransfer: { files: [new File(['x'], 'a.txt')] } });
    await user.click(await screen.findByRole('button', { name: 'Upload 1 file' }));
    await screen.findByText(/Uploading/);
    fireEvent.drop(zone, { dataTransfer: { files: [new File(['x'], 'b.txt')] } });
    fireEvent.change(input, { target: { files: [new File(['x'], 'c.txt')] } });

    // The list is still the one being sent.
    expect(screen.getByText('1 file to upload')).toBeInTheDocument();
    expect(uploadDocuments).toHaveBeenCalledTimes(1);
    finish({ corpus: 'library', stored: [stored('a.txt')], failed: [], job: {} });
    expect(await screen.findByText('1 file stored.')).toBeInTheDocument();
    expect(uploadDocuments).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('region', { name: 'Files to upload' })).not.toBeInTheDocument();
  });

  it('says nothing about refused files when none were refused', async () => {
    uploadDocuments.mockResolvedValue({
      corpus: 'library', stored: [stored('notes.txt')], failed: [], job: {},
    });
    const { addAndUpload } = await renderReady();

    await addAndUpload(new File(['x'], 'notes.txt'));

    expect(await screen.findByText('1 file stored.')).toBeInTheDocument();
    expect(screen.queryByText(/not stored/)).not.toBeInTheDocument();
  });

  it("clears the previous batch's refusals when the next upload starts", async () => {
    uploadDocuments.mockResolvedValueOnce({
      corpus: 'library', stored: [stored('a.txt')], failed: [{ file: 'b.bin', error: "'b.bin' is empty." }], job: {},
    });
    uploadDocuments.mockResolvedValueOnce({
      corpus: 'library', stored: [stored('c.txt')], failed: [], job: {},
    });
    const { addAndUpload } = await renderReady();

    await addAndUpload(new File(['x'], 'a.txt'));
    expect(await screen.findByText('1 file not stored:')).toBeInTheDocument();
    await addAndUpload(new File(['x'], 'c.txt'));

    await waitFor(() => expect(screen.queryByText(/not stored/)).not.toBeInTheDocument());
  });
});

describe('the list of files to upload', () => {
  const user = () => userEvent.setup();

  async function staged() {
    const u = user();
    const { container } = render(<DocumentsView corpora={[corpus('library')]} {...noop} />);
    await waitFor(() => expect(screen.getByRole('button', { name: /choose files/i })).toBeEnabled());
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    return { u, input };
  }

  it('uploads nothing when files are added, and shows each with its size', async () => {
    const { u, input } = await staged();

    await u.upload(input, [new File(['x'.repeat(2048)], 'notes.txt'), new File(['y'], 'more.md')]);

    const list = await screen.findByRole('region', { name: 'Files to upload' });
    expect(within(list).getByText('2 files to upload')).toBeInTheDocument();
    expect(within(list).getByText('notes.txt')).toBeInTheDocument();
    expect(within(list).getByText('2.0 KB')).toBeInTheDocument();
    expect(uploadDocuments).not.toHaveBeenCalled();
  });

  it('takes a drop into the list without sending it', async () => {
    await staged();
    const zone = screen.getByText('Drop files here').parentElement as HTMLElement;

    fireEvent.drop(zone, { dataTransfer: { files: [new File(['x'], 'a.txt')] } });

    expect(await screen.findByText('1 file to upload')).toBeInTheDocument();
    expect(uploadDocuments).not.toHaveBeenCalled();
  });

  it('does not add the same file twice within one drop either', async () => {
    const { u, input } = await staged();

    await u.upload(input, [
      new File(['x'], 'notes.txt', { lastModified: 1000 }),
      new File(['x'], 'notes.txt', { lastModified: 1000 }),
      new File(['x'], 'notes.txt', { lastModified: 2000 }),
    ]);

    expect(await screen.findByText('2 files to upload')).toBeInTheDocument();
  });

  it('keeps the list, and Upload ready again, when the whole upload fails', async () => {
    const onError = vi.fn();
    uploadDocuments.mockRejectedValue(new Error('Service Unavailable'));
    const u = userEvent.setup();
    const { container } = render(<DocumentsView corpora={[corpus('library')]} onRefresh={async () => {}} onError={onError} />);
    await u.upload(container.querySelector('input[type="file"]') as HTMLInputElement, [new File(['x'], 'a.txt'), new File(['x'], 'b.txt')]);

    await u.click(await screen.findByRole('button', { name: 'Upload 2 files' }));

    await waitFor(() => expect(onError).toHaveBeenCalledWith(expect.objectContaining({ message: 'Service Unavailable' })));
    expect(screen.getByText('2 files to upload')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Upload 2 files' })).toBeEnabled();
    expect(localStorage.getItem('dexicon.uploadTarget')).toBeNull();
  });

  it('adds to the list on a second drop, and does not add the same file twice', async () => {
    const { u, input } = await staged();
    const same = new File(['x'], 'notes.txt', { lastModified: 1000 });

    await u.upload(input, same);
    await u.upload(input, new File(['x'], 'notes.txt', { lastModified: 1000 }));
    await u.upload(input, new File(['x'], 'other.txt', { lastModified: 1000 }));

    expect(await screen.findByText('2 files to upload')).toBeInTheDocument();
  });

  it('removes one file, or all of them', async () => {
    const { u, input } = await staged();
    await u.upload(input, [new File(['x'], 'a.txt'), new File(['x'], 'b.txt')]);

    await u.click(await screen.findByRole('button', { name: 'Remove a.txt' }));
    expect(screen.getByText('1 file to upload')).toBeInTheDocument();
    expect(screen.queryByText('a.txt')).not.toBeInTheDocument();

    await u.click(screen.getByRole('button', { name: 'Clear' }));
    expect(screen.queryByRole('region', { name: 'Files to upload' })).not.toBeInTheDocument();
    expect(uploadDocuments).not.toHaveBeenCalled();
  });

  it('sends the files in the list, in order, to the corpus chosen, and empties the list', async () => {
    uploadDocuments.mockResolvedValue({ corpus: 'library', stored: [{ fileName: 'a.txt', deduplicated: false }, { fileName: 'b.txt', deduplicated: false }], failed: [], job: {} });
    const { u, input } = await staged();
    await u.upload(input, [new File(['x'], 'a.txt'), new File(['x'], 'b.txt')]);

    await u.click(await screen.findByRole('button', { name: 'Upload 2 files' }));

    expect(await screen.findByText('2 files stored.')).toBeInTheDocument();
    expect(uploadDocuments).toHaveBeenCalledTimes(1);
    expect(uploadDocuments.mock.calls[0][0]).toBe('library');
    expect((uploadDocuments.mock.calls[0][1] as File[]).map((f) => f.name)).toEqual(['a.txt', 'b.txt']);
    expect(screen.queryByRole('region', { name: 'Files to upload' })).not.toBeInTheDocument();
  });

  it('is not shown, and the choice of corpus with it, until there are files', async () => {
    await staged();

    expect(screen.queryByRole('region', { name: 'Files to upload' })).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Add to')).not.toBeInTheDocument();
  });
});

describe('which corpus the files go into', () => {
  async function stage(corpora: Corpus[]) {
    const u = userEvent.setup();
    const { container } = render(<DocumentsView corpora={corpora} {...noop} />);
    await u.upload(container.querySelector('input[type="file"]') as HTMLInputElement, new File(['x'], 'notes.txt'));
    return { u, add: await screen.findByLabelText('Add to') };
  }

  it('is the only corpus there is', async () => {
    const { add } = await stage([corpus('library')]);

    expect(add).toHaveTextContent('library');
  });

  it('is none of several until one is chosen', async () => {
    const { add } = await stage([corpus('library'), corpus('fine')]);

    expect(add).toHaveTextContent(/choose a corpus/i);
  });

  it('is the one last uploaded into, when it is still there', async () => {
    localStorage.setItem('dexicon.uploadTarget', 'fine');

    const { add } = await stage([corpus('library'), corpus('fine')]);

    expect(add).toHaveTextContent('fine');
  });

  it('is not a remembered corpus that has gone', async () => {
    localStorage.setItem('dexicon.uploadTarget', 'gone');

    const { add } = await stage([corpus('library'), corpus('fine')]);

    expect(add).toHaveTextContent(/choose a corpus/i);
  });

  it('is cleared when the corpus chosen is no longer there', async () => {
    const u = userEvent.setup();
    const { container, rerender } = render(<DocumentsView corpora={[corpus('library'), corpus('fine'), corpus('third')]} {...noop} />);
    await u.upload(container.querySelector('input[type="file"]') as HTMLInputElement, new File(['x'], 'notes.txt'));
    await u.click(await screen.findByLabelText('Add to'));
    await u.click(await screen.findByRole('option', { name: 'fine' }));
    expect(screen.getByRole('button', { name: 'Upload 1 file' })).toBeEnabled();

    rerender(<DocumentsView corpora={[corpus('library'), corpus('third')]} {...noop} />);

    expect(await screen.findByLabelText('Add to')).toHaveTextContent(/choose a corpus/i);
    expect(screen.getByRole('button', { name: 'Upload 1 file' })).toBeDisabled();
  });

  it('is remembered once files have been uploaded into it', async () => {
    uploadDocuments.mockResolvedValue({ corpus: 'fine', stored: [{ fileName: 'notes.txt', deduplicated: false }], failed: [], job: {} });
    const { u } = await stage([corpus('library'), corpus('fine')]);

    await u.click(screen.getByLabelText('Add to'));
    await u.click(await screen.findByRole('option', { name: 'fine' }));
    await u.click(screen.getByRole('button', { name: 'Upload 1 file' }));

    expect(await screen.findByText('1 file stored.')).toBeInTheDocument();
    expect(localStorage.getItem('dexicon.uploadTarget')).toBe('fine');
  });
});

/**
 * Detaching deletes a document that no other corpus holds, bytes and extracted text included, and it was one
 * click. It asks first, and says which of the two it is about to do.
 */
describe('detaching a document', () => {
  it('asks first, names what stays, and calls nothing on cancel', async () => {
    const user = userEvent.setup();
    render(<DocumentsView corpora={[corpus('library'), corpus('fine')]} {...noop} />);

    await user.click((await screen.findAllByRole('button', { name: 'Detach' }))[0]);
    const dialog = await screen.findByRole('dialog', { name: 'Detach moby-dick.epub from library?' });

    expect(dialog).toHaveTextContent('It stays stored for 1 other corpus');
    expect(detachDocument).not.toHaveBeenCalled();
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(detachDocument).not.toHaveBeenCalled();
  });

  it('detaches once confirmed, and reloads the library', async () => {
    const user = userEvent.setup();
    detachDocument.mockResolvedValue(undefined);
    render(<DocumentsView corpora={[corpus('library'), corpus('fine')]} {...noop} />);

    await user.click((await screen.findAllByRole('button', { name: 'Detach' }))[0]);
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Detach' }));

    expect(detachDocument).toHaveBeenCalledWith('library', 'f1');
    await waitFor(() => expect(listDocuments).toHaveBeenCalledTimes(2));
  });

  it('says the document is deleted when no other corpus holds it', async () => {
    const user = userEvent.setup();
    listDocuments.mockResolvedValue([{ ...doc, attachments: [doc.attachments[0]] }]);
    render(<DocumentsView corpora={[corpus('library')]} {...noop} />);

    await user.click(await screen.findByRole('button', { name: 'Detach' }));
    const dialog = await screen.findByRole('dialog');

    expect(dialog).toHaveTextContent(/No other corpus holds this document, so it is deleted/);
    expect(dialog).toHaveTextContent(/Upload it again to restore it/);
  });

  it('shows a refusal in the dialog that asked', async () => {
    const user = userEvent.setup();
    detachDocument.mockRejectedValue(new ApiError(403, 'Insufficient scope', 'This key needs destroy.'));
    render(<DocumentsView corpora={[corpus('library'), corpus('fine')]} {...noop} />);

    await user.click((await screen.findAllByRole('button', { name: 'Detach' }))[0]);
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Detach' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(/needs destroy/);
  });
});

describe('an attachment that is still pending', () => {
  const pendingDoc = {
    ...doc,
    attachments: [{ ...doc.attachments[0], status: 'pending', chunkCount: 0 }],
  } as unknown as LibraryDocument;
  const indexedDoc = {
    ...doc,
    attachments: [{ ...doc.attachments[0], status: 'indexed', chunkCount: 980 }],
  } as unknown as LibraryDocument;

  afterEach(() => {
    vi.useRealTimers();
  });

  it('is read again until it is indexed, and then the list is left alone', async () => {
    // The row read "pending 0 chunks" until the page was left and entered again, though the
    // pass that chunked it finished within seconds.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    listDocuments.mockResolvedValueOnce([pendingDoc]).mockResolvedValue([indexedDoc]);
    render(<DocumentsView corpora={[corpus('library')]} {...noop} />);

    expect(await screen.findByText('pending')).toBeInTheDocument();
    expect(listDocuments).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(3500);
    expect(await screen.findByText('980 chunks')).toBeInTheDocument();
    expect(screen.queryByText('pending')).not.toBeInTheDocument();
    expect(listDocuments).toHaveBeenCalledTimes(2);

    await vi.advanceTimersByTimeAsync(20_000);
    expect(listDocuments).toHaveBeenCalledTimes(2);
  });

  it('does not read the list again when nothing is pending', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    listDocuments.mockResolvedValue([indexedDoc]);
    render(<DocumentsView corpora={[corpus('library')]} {...noop} />);

    expect(await screen.findByText('980 chunks')).toBeInTheDocument();
    await vi.advanceTimersByTimeAsync(20_000);

    expect(listDocuments).toHaveBeenCalledTimes(1);
  });

  it('tries again after a failed read without raising an error of its own', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    listDocuments
      .mockResolvedValueOnce([pendingDoc])
      .mockRejectedValueOnce(new Error('connection closed'))
      .mockResolvedValue([indexedDoc]);
    render(<DocumentsView corpora={[corpus('library')]} {...noop} />);
    await screen.findByText('pending');

    await vi.advanceTimersByTimeAsync(3500);
    expect(listDocuments).toHaveBeenCalledTimes(2);
    expect(noop.onError).not.toHaveBeenCalled();
    expect(screen.getByText('pending')).toBeInTheDocument();

    await vi.advanceTimersByTimeAsync(3500);
    expect(await screen.findByText('980 chunks')).toBeInTheDocument();
    expect(noop.onError).not.toHaveBeenCalled();
  });
});