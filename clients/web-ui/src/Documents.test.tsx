import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DocumentsView } from './Documents';
import type { Corpus, LibraryDocument } from './api';

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

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: {
    listDocuments: (...a: unknown[]) => listDocuments(...a),
    uploadDocuments: (...a: unknown[]) => uploadDocuments(...a),
    attachDocument: (...a: unknown[]) => attachDocument(...a),
    detachDocument: vi.fn(),
    documentText: vi.fn(),
  },
}));

function corpus(name: string, over: Partial<Corpus> = {}): Corpus {
  return {
    id: `id-${name}`,
    name,
    description: null,
    tenantId: 'default',
    owned: true,
    visibility: 'private',
    state: 'ready',
    createdUtc: new Date().toISOString(),
    lastIndexedUtc: null,
    sourceCount: 0,
    fileCount: 0,
    chunkCount: 0,
    skippedCount: 0,
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
    expect(within(card).getByText(/from 768\/100 language-aware/)).toBeInTheDocument();
    // Formatted the way the component formats it. The literal used to be hard-coded with
    // a `.replace(',', ',')` beside it, which replaced a comma with a comma and did
    // nothing; the separator it was presumably meant to normalise is locale-dependent,
    // so under de-DE or fr-FR this asserted on a string the component never renders.
    expect(within(card).getByText(`${(2940).toLocaleString()} chunks`)).toBeInTheDocument();
    expect(within(card).getByText(/from 256\/40 blank-line/)).toBeInTheDocument();
  });

  it('will not upload until a destination is chosen', async () => {
    // Upload goes into ONE corpus. A button that is live before the target is picked is
    // an invitation to put a file somewhere at random.
    render(<DocumentsView corpora={[]} {...noop} />);

    expect(await screen.findByRole('button', { name: /choose files/i })).toBeDisabled();
  });

  it('says there is nowhere to put a file rather than offering an empty picker', async () => {
    render(<DocumentsView corpora={[]} {...noop} />);

    expect(await screen.findByLabelText(/upload into/i)).toHaveTextContent(/no writable corpus/i);
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
    // "Nothing is re-uploaded" is the reassurance; "chunked as default (768/100)" is the
    // consequence. Both belong on screen before the button is pressed.
    const user = userEvent.setup();
    render(<DocumentsView corpora={[corpus('library'), corpus('archive')]} {...noop} />);

    await user.click(await screen.findByRole('button', { name: /attach to another corpus/i }));
    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).getByText(/nothing is re-uploaded/i)).toBeInTheDocument();
    await waitFor(() =>
      expect(within(dialog).getByText(/768\/100/)).toBeInTheDocument(),
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
});
