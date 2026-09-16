import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ChunkSetsPanel } from './ChunkSets';
import type { ChunkSet, Corpus, EmbeddingModelInfo } from './api';

/**
 * The chunk set screen, and the bugs it shipped with.
 *
 * Every field in the "Add a chunk set" modal rendered with no border, no background and
 * no padding; the primary button was indistinguishable from Cancel; and the model
 * dropdown listed the same model twice. Types were clean, the build was clean, 163 server
 * tests passed. It was found by someone looking at a screenshot.
 *
 * jsdom computes no layout, so none of this asserts "visible" — it asserts the mechanism
 * that made it invisible. That is most of the value for none of the cost of a real browser.
 */

// The panel asks the server which models exist. Mocked: this is a test of what the
// component renders, not of the network.
const listEmbeddingModels = vi.fn();
const listEmbeddingProviders = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: {
    listEmbeddingModels: (...args: unknown[]) => listEmbeddingModels(...args),
    listEmbeddingProviders: (...args: unknown[]) => listEmbeddingProviders(...args),
  },
}));

// Typed, not cast: if a field leaves ChunkSetSummary this file stops compiling, which is
// how the last drift (Corpus still declaring chunkSize) should have been caught.
function chunkSet(over: Partial<ChunkSet> = {}): ChunkSet {
  return {
    id: 'set-default',
    name: 'default',
    description: null,
    embeddingProvider: 'ollama',
    embeddingModel: 'nomic-embed-text',
    embeddingDimensions: 768,
    collectionName: 'dexicon__nomic-embed-text__768',
    chunkSize: 768,
    chunkOverlap: 100,
    boundaryMode: 'language-aware',
    customBoundaryPattern: null,
    unitAware: false,
    sentenceAware: false,
    headingContext: false,
    isDefault: true,
    state: 'ready',
    fileCount: 12,
    chunkCount: 110,
    pendingCount: 0,
    failedCount: 0,
    createdUtc: new Date().toISOString(),
    lastIndexedUtc: new Date().toISOString(),
    ...over,
  };
}

function corpus(chunkSets: ChunkSet[]): Corpus {
  return {
    id: 'c1',
    name: 'docs',
    description: null,
    tenantId: 'default',
    owned: true,
    visibility: 'private',
    state: 'ready',
    createdUtc: new Date().toISOString(),
    lastIndexedUtc: null,
    sourceCount: 1,
    fileCount: 12,
    chunkCount: 110,
    skippedCount: 0,
    failedCount: 0,
    sources: [],
    chunkSets,
  };
}

function model(name: string, over: Partial<EmbeddingModelInfo> = {}): EmbeddingModelInfo {
  return {
    name,
    sizeBytes: 274_000_000,
    dimensions: 768,
    inUse: true,
    documentTemplate: 'search_document: {text}',
    queryTemplate: 'search_query: {text}',
    templateOrigin: 'builtin',
    ...over,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  listEmbeddingModels.mockResolvedValue({
    provider: 'ollama',
    managed: true,
    configured: 'nomic-embed-text',
    models: [model('nomic-embed-text:latest'), model('embeddinggemma:latest')],
    note: null,
  });
  listEmbeddingProviders.mockResolvedValue({ default: 'ollama', providers: [] });
});

/** Opens the add modal and waits for the model list to arrive, so no state lands late. */
async function openAddModal(sets: ChunkSet[] = [chunkSet()]) {
  const user = userEvent.setup();
  render(<ChunkSetsPanel corpus={corpus(sets)} onChanged={vi.fn()} />);

  await user.click(screen.getByRole('button', { name: /add set/i }));
  const dialog = await screen.findByRole('dialog');

  // The model field starts as a free-text Input and becomes a Select once the list loads.
  await waitFor(() =>
    expect(within(dialog).getByLabelText(/Embedding model/).tagName).toBe('SELECT'),
  );

  return { dialog, user };
}

describe('the Add a chunk set form', () => {
  it('renders every field as a real, styled control', async () => {
    const { dialog } = await openAddModal();

    for (const label of [/^Name/, /Chunk size/, /Overlap/, /Boundary mode/, /Embedding model/]) {
      expect(within(dialog).getByLabelText(label)).toHaveClass('input');
    }
  });

  it('makes the confirming button a primary one', async () => {
    const { dialog } = await openAddModal();

    // "Add set" and "Cancel" rendered identically, because the class was `btn primary`
    // and the stylesheet defines `btn-primary`.
    expect(within(dialog).getByRole('button', { name: 'Add set' })).toHaveClass('btn-primary');
    expect(within(dialog).getByRole('button', { name: 'Cancel' })).not.toHaveClass('btn-primary');
  });

  it('lists each embedding model once', async () => {
    // A chunk set stores `nomic-embed-text`; the provider lists `nomic-embed-text:latest`.
    // The "stored value is not in the list" fallback compared them raw, so the model the
    // set already used appeared twice in the dropdown.
    const { dialog } = await openAddModal();

    const options = [...within(dialog).getByLabelText(/Embedding model/).querySelectorAll('option')];
    const names = options.map((o) => o.getAttribute('value')?.replace(/:latest$/, ''));

    expect(names).toEqual(['nomic-embed-text', 'embeddinggemma']);
  });

  it('will not add a set without a name', async () => {
    // The name is how search addresses the set. There is no sensible default for it.
    const { dialog } = await openAddModal();
    expect(within(dialog).getByRole('button', { name: 'Add set' })).toBeDisabled();
  });

  it('says that adding a set does not change search until it is promoted', async () => {
    // The whole safety story of the two-step migration. Without it, someone assumes
    // adding a set has already changed what search returns.
    const { dialog } = await openAddModal();
    expect(within(dialog).getByText(/until you promote/i)).toBeInTheDocument();
  });

  it('reveals the pattern box only for a custom boundary', async () => {
    const { dialog, user } = await openAddModal();

    expect(within(dialog).queryByLabelText(/Boundary pattern/)).not.toBeInTheDocument();

    await user.selectOptions(within(dialog).getByLabelText(/Boundary mode/), 'custom');

    expect(within(dialog).getByLabelText(/Boundary pattern/)).toHaveClass('input');
  });
});

describe('the chunk set list', () => {
  const twoSets = (over: Partial<ChunkSet> = {}) => [
    chunkSet(),
    chunkSet({ id: 's2', name: 'fine', isDefault: false, ...over }),
  ];

  it('names each set the way search addresses it', () => {
    // `corpus:set` is what an agent passes to search_index. A bare name teaches nobody
    // that the addressing exists.
    render(<ChunkSetsPanel corpus={corpus(twoSets())} onChanged={vi.fn()} />);
    expect(screen.getByText('docs:fine')).toBeInTheDocument();
  });

  it('refuses to promote a set that is still building, and says why', () => {
    // Promoting a half-built set is the incomplete-search outage that building it
    // separately exists to prevent.
    render(
      <ChunkSetsPanel
        corpus={corpus(twoSets({ pendingCount: 10, chunkCount: 40 }))}
        onChanged={vi.fn()}
      />,
    );

    const promote = screen.getByRole('button', { name: 'Promote' });
    expect(promote).toBeDisabled();
    // Disabled rather than hidden: the reason is the point.
    expect(promote).toHaveAttribute('title', expect.stringContaining('10'));
  });

  it('offers promotion once the set is complete', () => {
    render(
      <ChunkSetsPanel
        corpus={corpus(twoSets({ pendingCount: 0, chunkCount: 361 }))}
        onChanged={vi.fn()}
      />,
    );

    expect(screen.getByRole('button', { name: 'Promote' })).toBeEnabled();
  });

  it('does not offer to promote or delete the set search already uses', () => {
    render(<ChunkSetsPanel corpus={corpus(twoSets())} onChanged={vi.fn()} />);

    // One Promote and one Delete, both belonging to the non-default set.
    expect(screen.getAllByRole('button', { name: 'Promote' })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'Delete' })).toHaveLength(1);
  });

  it('does not offer to delete the only set', () => {
    // A corpus with no chunk sets cannot be searched at all.
    render(<ChunkSetsPanel corpus={corpus([chunkSet()])} onChanged={vi.fn()} />);
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
  });
});
