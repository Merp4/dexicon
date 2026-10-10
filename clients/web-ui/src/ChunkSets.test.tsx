import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ChunkSetsPanel, ModelsView } from './ChunkSets';
import { ApiError, type ChunkSet, type Corpus, type EmbeddingModelInfo } from './api';

/**
 * The chunk set screen, and the bugs it shipped with.
 *
 * Every field in the "Add a chunk set" modal rendered with no border, no background and
 * no padding; the primary button was indistinguishable from Cancel; and the model
 * dropdown listed the same model twice. Types were clean, the build was clean, 163 server
 * tests passed. It was found by someone looking at a screenshot.
 *
 * jsdom computes no layout, so none of this asserts "visible"; it asserts that each
 * control is the component library's, wearing the variant asked for, which is the thing
 * that was actually wrong.
 */

// The panel asks the server which models exist. Mocked: this is a test of what the
// component renders, not of the network.
const listEmbeddingModels = vi.fn();
const probeEmbeddingModel = vi.fn();
const listEmbeddingProviders = vi.fn();
const promoteChunkSet = vi.fn();
const updateChunkSet = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: {
    listEmbeddingModels: (...args: unknown[]) => listEmbeddingModels(...args),
    probeEmbeddingModel: (...args: unknown[]) => probeEmbeddingModel(...args),
    listEmbeddingProviders: (...args: unknown[]) => listEmbeddingProviders(...args),
    promoteChunkSet: (...args: unknown[]) => promoteChunkSet(...args),
    updateChunkSet: (...args: unknown[]) => updateChunkSet(...args),
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
    state: 'ready',
    createdUtc: new Date().toISOString(),
    lastIndexedUtc: null,
    sourceCount: 1,
    fileCount: 12,
    chunkCount: 110,
    skippedCount: 0,
    pendingCount: 0,
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
    // Never probed, which is the ordinary case and the one where the form must not
    // invent a suggestion.
    measured: null,
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

/** Opens the add modal and waits for the model list, so no state lands after the test. */
async function openAddModal(sets: ChunkSet[] = [chunkSet()]) {
  const user = userEvent.setup();
  render(<ChunkSetsPanel corpus={corpus(sets)} onChanged={vi.fn()} open onOpenChange={vi.fn()} />);

  await user.click(screen.getByRole('button', { name: /add set/i }));
  const dialog = await screen.findByRole('dialog');
  await waitFor(() => expect(listEmbeddingModels).toHaveBeenCalled());

  return { dialog, user };
}

/** The options behind a closed select, which only exist once it is opened. */
async function optionsOf(user: ReturnType<typeof userEvent.setup>, trigger: HTMLElement) {
  await user.click(trigger);
  const listbox = await screen.findByRole('listbox');
  return within(listbox).getAllByRole('option').map((o) => o.textContent ?? '');
}

describe('the Add a chunk set form', () => {
  it('renders every field as a real control', async () => {
    const { dialog } = await openAddModal();

    for (const label of ['Name', 'Chunk size (tokens)', 'Overlap (tokens)']) {
      expect(within(dialog).getByLabelText(label)).toHaveAttribute('data-slot', 'input');
    }
    for (const label of ['Boundary mode', 'Embedding model']) {
      expect(within(dialog).getByLabelText(label)).toHaveAttribute(
        'data-slot',
        'select-trigger',
      );
    }
  });

  it('offers each field hint as a description rather than as its name', async () => {
    const { dialog } = await openAddModal();

    expect(within(dialog).getByLabelText('Overlap (tokens)')).toHaveAccessibleDescription(
      'Must be smaller than the chunk size.',
    );
  });

  it('makes the confirming button the primary one', async () => {
    const { dialog } = await openAddModal();

    // "Add set" and "Cancel" rendered identically, because the class was `btn primary`
    // and the stylesheet defines `btn-primary`.
    const confirm = within(dialog).getByRole('button', { name: 'Add set' });
    const cancel = within(dialog).getByRole('button', { name: 'Cancel' });

    expect(confirm).toHaveAttribute('data-variant', 'default');
    expect(cancel.dataset.variant).not.toBe(confirm.dataset.variant);
  });

  it('lists each embedding model once', async () => {
    // A chunk set stores `nomic-embed-text`; the provider lists `nomic-embed-text:latest`.
    // The "stored value is not in the list" fallback compared them raw, so the model the
    // set already used appeared twice.
    const { dialog, user } = await openAddModal();

    const values = await optionsOf(user, within(dialog).getByLabelText('Embedding model'));

    expect(values).toEqual([
      'nomic-embed-text:latest (261.3 MB) · in use',
      'embeddinggemma:latest (261.3 MB) · in use',
    ]);
  });

  it('inherits the model of the set search currently uses', async () => {
    // A new set is almost always "the same, but chunked differently". Starting blank
    // makes the common case the most typing.
    const { dialog } = await openAddModal();

    expect(within(dialog).getByLabelText('Embedding model')).toHaveTextContent(
      'nomic-embed-text',
    );
  });

  it('suggests the chunk size the chosen model was measured at', async () => {
    // The field said "64-8192" with no reference to the selected model, and the probe
    // that knows the answer threw it away on reload. A measurement nobody can reach is
    // the same as no measurement.
    listEmbeddingModels.mockResolvedValue({
      provider: 'ollama', managed: true, configured: 'mxbai-embed-large', note: null,
      models: [model('mxbai-embed-large:latest', {
        measured: {
          maxInputChars: 2816, truncatesSilently: true, recommendedChunkTokens: 665,
          charsPerToken: 2.82, measuredUtc: new Date().toISOString(),
        },
      })],
    });

    const { dialog } = await openAddModal([chunkSet({ embeddingModel: 'mxbai-embed-large' })]);

    await waitFor(() =>
      expect(within(dialog).getByLabelText('Chunk size (tokens)')).toHaveValue(665));
  });

  it('suggests no more than a chunk set accepts for a model with a very long context', async () => {
    // 0.9 of a 32k context is 29,491 tokens, and the server refuses a size over 8,192. The size, the
    // overlap derived from it and the hint all go through the same limit.
    listEmbeddingModels.mockResolvedValue({
      provider: 'ollama', managed: true, configured: 'qwen3-embedding', note: null,
      models: [model('qwen3-embedding:latest', {
        measured: {
          maxInputChars: 131_072, truncatesSilently: false, recommendedChunkTokens: 29_491,
          charsPerToken: 4, measuredUtc: new Date().toISOString(),
        },
      })],
    });

    const { dialog } = await openAddModal([chunkSet({ embeddingModel: 'qwen3-embedding' })]);

    await waitFor(() =>
      expect(within(dialog).getByLabelText('Chunk size (tokens)')).toHaveValue(8192));
    expect(within(dialog).getByLabelText('Overlap (tokens)')).toHaveValue(1024);
    expect(within(dialog).getByText(/suggested 8,192$/)).toBeInTheDocument();
  });

  it('sends only what was changed, so a set stored with an old size can still be edited', async () => {
    // The corpus dialog used to send 29,491 tokens for a 32k model, and the server now refuses a size over
    // 8,192. Sending the stored size back with every save made every edit of such a set a 400.
    updateChunkSet.mockResolvedValue({});
    const user = userEvent.setup();
    const onChanged = vi.fn();
    render(
      <ChunkSetsPanel
        corpus={corpus([chunkSet({ chunkSize: 29_491, chunkOverlap: 3_686, description: 'old' })])}
        onChanged={onChanged}
        open
        onOpenChange={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    const description = within(dialog).getByLabelText('Description');
    await user.clear(description);
    await user.type(description, 'read for the fine search');
    await user.click(within(dialog).getByRole('button', { name: 'Save and re-chunk' }));

    await waitFor(() => expect(updateChunkSet).toHaveBeenCalled());
    const sent = updateChunkSet.mock.calls[0][2];
    expect(sent).toEqual({ description: 'read for the fine search' });
    expect(Object.keys(sent).filter((k) => sent[k] !== undefined)).toEqual(['description']);
  });

  it('leaves the description out of the request when only another setting was edited', async () => {
    updateChunkSet.mockResolvedValue({});
    const user = userEvent.setup();
    render(
      <ChunkSetsPanel
        corpus={corpus([chunkSet({ chunkSize: 29_491, chunkOverlap: 3_686, description: 'old', headingContext: false })])}
        onChanged={vi.fn()}
        open
        onOpenChange={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByLabelText('Heading context'));
    await user.click(within(dialog).getByRole('button', { name: 'Save and re-chunk' }));

    await waitFor(() => expect(updateChunkSet).toHaveBeenCalled());
    const sent = updateChunkSet.mock.calls[0][2];
    expect(Object.keys(sent).filter((k) => sent[k] !== undefined)).toEqual(['headingContext']);
  });

  it('leaves the size alone for a model that has never been probed', async () => {
    // No measurement is not a licence to guess. The set being copied stays the reference.
    const { dialog } = await openAddModal();

    expect(within(dialog).getByLabelText('Chunk size (tokens)')).toHaveValue(768);
    expect(within(dialog).getByText(/Run Test limits/)).toBeInTheDocument();
  });

  it('warns when the size will silently truncate on the chosen model', async () => {
    // The concrete failure: mxbai accepts 2,816 characters and the default 768-token
    // budget produces 3,072, so every full chunk loses its end and nothing reports it.
    listEmbeddingModels.mockResolvedValue({
      provider: 'ollama', managed: true, configured: 'mxbai-embed-large', note: null,
      models: [model('mxbai-embed-large:latest', {
        measured: {
          maxInputChars: 2816, truncatesSilently: true, recommendedChunkTokens: 665,
          charsPerToken: 2.82, measuredUtc: new Date().toISOString(),
        },
      })],
    });

    const { dialog, user } = await openAddModal([chunkSet({ embeddingModel: 'mxbai-embed-large' })]);

    const size = within(dialog).getByLabelText('Chunk size (tokens)');
    await user.clear(size);
    await user.type(size, '768');

    expect(await within(dialog).findByText(/silently drop the end of every full chunk/))
      .toBeInTheDocument();
    expect(size).toHaveAttribute('aria-invalid', 'true');
  });

  it('does not overwrite a size somebody typed', async () => {
    // Typing is a decision. A later model change must not quietly undo it.
    const { dialog, user } = await openAddModal();

    const size = within(dialog).getByLabelText('Chunk size (tokens)');
    await user.clear(size);
    await user.type(size, '512');

    expect(size).toHaveValue(512);
  });

  it('will not add a set without a name', async () => {
    // The name is how search addresses the set; there is no sensible default for it.
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

    await user.click(within(dialog).getByLabelText('Boundary mode'));
    await user.click(await screen.findByRole('option', { name: /custom/ }));

    expect(within(dialog).getByLabelText('Boundary pattern')).toHaveAttribute(
      'data-slot',
      'input',
    );
  });
});

describe('the chunk sets, collapsed', () => {
  const collapsed = (sets: ChunkSet[], onOpenChange = vi.fn()) =>
    render(<ChunkSetsPanel corpus={corpus(sets)} onChanged={vi.fn()} open={false} onOpenChange={onOpenChange} />);

  it('names the set search uses, its model and its size, and hides the list', () => {
    collapsed([chunkSet(), chunkSet({ id: 's2', name: 'fine', isDefault: false })]);

    const toggle = screen.getByRole('button', { name: /Chunk sets/ });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(toggle).toHaveTextContent('docs:default');
    expect(toggle).toHaveTextContent('nomic-embed-text (768d) · 768 / 100 · 110 chunks');
    expect(toggle).toHaveTextContent('+1 more');
    expect(screen.queryByRole('button', { name: 'Promote' })).not.toBeInTheDocument();
  });

  it('has an accessible name with its parts apart', () => {
    // The gaps are flex gaps, which are visual only, so the name was the text run
    // together: "Chunk setsdocs:defaultnomic-embed-text…".
    collapsed([chunkSet(), chunkSet({ id: 's2', name: 'fine', isDefault: false, state: 'indexing', pendingCount: 40 })]);

    expect(screen.getByRole('button', { name: /Chunk sets/ })).toHaveAccessibleName(
      'Chunk sets docs:default nomic-embed-text (768d) · 768 / 100 · 110 chunks ready +1 more fine: indexing, 40 pending',
    );
  });

  it('still shows a set that is building, so collapsing hides no work in progress', () => {
    collapsed([chunkSet(), chunkSet({ id: 's2', name: 'fine', isDefault: false, state: 'indexing', pendingCount: 40 })]);

    expect(screen.getByRole('button', { name: /Chunk sets/ })).toHaveTextContent('fine: indexing, 40 pending');
  });

  it('colours a set in the line as the open list colours it', () => {
    // An unavailable set is red in the list; the collapsed line showed it amber.
    const sets = [chunkSet(), chunkSet({ id: 's2', name: 'fine', isDefault: false, state: 'unavailable' })];
    const { rerender } = render(
      <ChunkSetsPanel corpus={corpus(sets)} onChanged={vi.fn()} open={false} onOpenChange={vi.fn()} />,
    );
    const inLine = screen.getByText('fine: unavailable').className;

    rerender(<ChunkSetsPanel corpus={corpus(sets)} onChanged={vi.fn()} open onOpenChange={vi.fn()} />);

    expect(screen.getByText('unavailable', { exact: true }).className).toBe(inLine);
  });

  it('keeps an error from an action in view after the list is closed', async () => {
    promoteChunkSet.mockRejectedValue(new Error('the vector store is unreachable'));
    const sets = [chunkSet(), chunkSet({ id: 's2', name: 'fine', isDefault: false })];
    const { rerender } = render(
      <ChunkSetsPanel corpus={corpus(sets)} onChanged={vi.fn()} open onOpenChange={vi.fn()} />,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Promote' }));
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Promote' }));
    await screen.findByText(/the vector store is unreachable/);
    rerender(<ChunkSetsPanel corpus={corpus(sets)} onChanged={vi.fn()} open={false} onOpenChange={vi.fn()} />);

    expect(screen.getByText(/the vector store is unreachable/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Promote' })).not.toBeInTheDocument();
  });

  it.each([
    [404, true],
    [409, true],
    [500, false],
  ])('reads the list again after a %i answer to an action: %s', async (status, again) => {
    // A set that is gone, or one kept without its vectors, is not what the list showed before the request.
    promoteChunkSet.mockRejectedValue(new ApiError(status, 'Refused', 'it changed while this request waited.'));
    const onChanged = vi.fn();
    const sets = [chunkSet(), chunkSet({ id: 's2', name: 'fine', isDefault: false })];
    render(<ChunkSetsPanel corpus={corpus(sets)} onChanged={onChanged} open onOpenChange={vi.fn()} />);

    await userEvent.click(screen.getByRole('button', { name: 'Promote' }));
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Promote' }));
    await screen.findByText(/it changed while this request waited/);

    expect(onChanged).toHaveBeenCalledTimes(again ? 1 : 0);
  });

  it('asks to open when its line is pressed', async () => {
    const onOpenChange = vi.fn();
    collapsed([chunkSet()], onOpenChange);

    await userEvent.click(screen.getByRole('button', { name: /Chunk sets/ }));

    expect(onOpenChange).toHaveBeenCalledWith(true);
  });

  it('can add a set without being opened first', async () => {
    collapsed([chunkSet()]);

    await userEvent.click(screen.getByRole('button', { name: '+ Add set' }));

    expect(await screen.findByRole('dialog', { name: 'Add a chunk set' })).toBeInTheDocument();
    await waitFor(() => expect(listEmbeddingModels).toHaveBeenCalled());
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
    render(<ChunkSetsPanel corpus={corpus(twoSets())} onChanged={vi.fn()} open onOpenChange={vi.fn()} />);
    expect(screen.getByText('docs:fine')).toBeInTheDocument();
  });

  it('refuses to promote a set that is still building, and says why', () => {
    // Promoting a half-built set is the incomplete-search outage that building it
    // separately exists to prevent.
    render(
      <ChunkSetsPanel
        corpus={corpus(twoSets({ pendingCount: 10, chunkCount: 40 }))}
        onChanged={vi.fn()}
        open
        onOpenChange={vi.fn()}
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
        open
        onOpenChange={vi.fn()}
      />,
    );

    expect(screen.getByRole('button', { name: 'Promote' })).toBeEnabled();
  });

  it('asks before promoting, naming the set search moves to and the one it leaves', async () => {
    // Promote changed what every search of the corpus returns on one click, with no
    // question, next to an Edit button of the same size.
    const user = userEvent.setup();
    const onChanged = vi.fn();
    promoteChunkSet.mockResolvedValue(undefined);
    render(
      <ChunkSetsPanel
        corpus={corpus(twoSets({ embeddingModel: 'embeddinggemma', chunkCount: 361 }))}
        onChanged={onChanged}
        open
        onOpenChange={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Promote' }));
    const dialog = await screen.findByRole('dialog', { name: 'Search docs with fine?' });
    expect(promoteChunkSet).not.toHaveBeenCalled();
    expect(dialog).toHaveTextContent(
      'Searches of docs will read docs:fine (embeddinggemma, 361 chunks) in place of docs:default (nomic-embed-text).',
    );

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(promoteChunkSet).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Promote' }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Promote' }));
    expect(promoteChunkSet).toHaveBeenCalledWith('docs', 'fine');
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
  });

  it('says "1 chunk" of a set holding one', () => {
    render(<ChunkSetsPanel corpus={corpus(twoSets({ chunkCount: 1, fileCount: 1 }))} onChanged={vi.fn()} open onOpenChange={vi.fn()} />);

    expect(screen.getByText(/1 file · 1 chunk ·/)).toBeInTheDocument();
  });

  it('does not offer to promote or delete the set search already uses', () => {
    render(<ChunkSetsPanel corpus={corpus(twoSets())} onChanged={vi.fn()} open onOpenChange={vi.fn()} />);

    // One Promote and one Delete, both belonging to the non-default set.
    expect(screen.getAllByRole('button', { name: 'Promote' })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'Delete' })).toHaveLength(1);
  });

  it('does not offer to delete the only set', () => {
    // A corpus with no chunk sets cannot be searched at all.
    render(<ChunkSetsPanel corpus={corpus([chunkSet()])} onChanged={vi.fn()} open onOpenChange={vi.fn()} />);
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
  });

  it('confirms a delete in a dialog of its own rather than the browser one', async () => {
    // This was `window.confirm`, which paints a box the page has no say over: it ignores
    // the theme, puts the destructive action wherever the browser likes, and after the
    // second one a browser offers to suppress further dialogs for the session — at which
    // point deleting a chunk set stops asking at all.
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false);
    const user = userEvent.setup();
    render(<ChunkSetsPanel corpus={corpus(twoSets({ chunkCount: 361 }))} onChanged={vi.fn()} open onOpenChange={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Delete' }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/361/)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: /delete chunk set/i })).toBeInTheDocument();
    expect(confirmSpy).not.toHaveBeenCalled();

    confirmSpy.mockRestore();
  });
});

/**
 * Measuring a model's limits, when the embedding service is busy.
 *
 * The probe embeds two dozen inputs one after another, and the embedding service answers
 * indexing first, so on a corpus that is mid-reindex it can run for the better part of an
 * hour. It had no deadline and no feedback: the button showed a spinner that never ended,
 * which is indistinguishable from a hang, and the only way out was to reload the page. The
 * server gave up at 499 with nobody watching.
 */
describe('measuring a model', () => {
  async function openModels() {
    const user = userEvent.setup();
    // ModelsView lists the models of a provider, so it needs one; the shared default has
    // an empty provider list because the panel under test elsewhere does not.
    listEmbeddingProviders.mockResolvedValue({
      default: 'ollama',
      providers: [{ name: 'ollama', kind: 'ollama', managed: true, configured: true, detail: null }],
    });
    render(<ModelsView />);
    await screen.findAllByRole('button', { name: /Test limits/ });
    return user;
  }

  /** The first model's button. The list holds two, and this is about one of them. */
  const testLimits = () => screen.getAllByRole('button', { name: /Test limits/ })[0];

  it('says how long it has been measuring, rather than spinning in silence', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      probeEmbeddingModel.mockReturnValue(new Promise(() => {}));   // never settles
      const user = await openModels();

      await user.click(testLimits());

      expect(await screen.findByRole('button', { name: /Stop \(0s\)/ })).toBeInTheDocument();
      await vi.advanceTimersByTimeAsync(3000);
      expect(await screen.findByRole('button', { name: /Stop \(3s\)/ })).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('suggests no more than a chunk set accepts after measuring a model with a very long context', async () => {
    probeEmbeddingModel.mockResolvedValue({
      model: 'nomic-embed-text:latest', dimensions: 1024, maxInputChars: 131_072, truncatesSilently: false,
      recommendedChunkChars: 117_964, recommendedChunkTokens: 29_491, charsPerToken: 4, contextTokens: 32_768,
      embedCalls: 24, tookMs: 1500, summary: 'accepts everything we tried',
    });

    const user = await openModels();
    await user.click(testLimits());

    expect(await screen.findByText(/suggested chunk size/)).toHaveTextContent('suggested chunk size 8,192 tokens');
  });

  it('can be stopped, and stopping is not reported as a failure', async () => {
    // The only way out used to be reloading the page.
    let abortSignal: AbortSignal | undefined;
    probeEmbeddingModel.mockImplementation((_m: string, _p: string, signal: AbortSignal) => {
      abortSignal = signal;
      return new Promise((_resolve, reject) => {
        signal.addEventListener('abort', () => reject(new Error('aborted')));
      });
    });

    const user = await openModels();
    await user.click(testLimits());
    await user.click(await screen.findByRole('button', { name: /Stop \(/ }));

    expect(abortSignal?.aborted).toBe(true);
    // Back to a button you can press again.
    await waitFor(() => expect(testLimits()).toBeEnabled());
    // Stopping something you started is not an error to put on the screen.
    expect(screen.queryByText(/aborted/i)).not.toBeInTheDocument();
  });

  it('surfaces why a probe timed out instead of leaving the spinner up', async () => {
    probeEmbeddingModel.mockRejectedValue(
      new Error('No answer within 90s. The probe embeds two dozen inputs, and the embedding '
        + 'service answers indexing first, so this usually means an index job is running.'));

    const user = await openModels();
    await user.click(testLimits());

    expect(await screen.findByText(/index job is running/)).toBeInTheDocument();
    await waitFor(() => expect(testLimits()).toBeEnabled());
  });
});

/**
 * The "default for new corpora" badge.
 *
 * `/api/embedding-models` answers with the configured default model for every provider it
 * lists, so a badge decided on the model name alone also marked a model of that name under a
 * provider that is not the default. A new corpus is created against the default provider and
 * model together.
 */
describe('the default for new corpora badge', () => {
  const providers = {
    default: 'ollama',
    providers: [
      { name: 'ollama', kind: 'ollama', managed: true, configured: true, detail: null },
      { name: 'openai', kind: 'openai', managed: false, configured: true, detail: null },
    ],
  };

  beforeEach(() => {
    listEmbeddingProviders.mockResolvedValue(providers);
    listEmbeddingModels.mockImplementation(async (provider: string) => ({
      provider,
      managed: provider === 'ollama',
      // The server's answer is the same for both: the configured default is one model name.
      configured: 'nomic-embed-text',
      models:
        provider === 'ollama'
          ? [model('nomic-embed-text:latest'), model('embeddinggemma:latest')]
          : [model('nomic-embed-text'), model('text-embedding-3-small')],
      note: null,
    }));
  });

  /** The rows that carry the badge, by model name. */
  const badged = () =>
    screen
      .getAllByText('default for new corpora')
      .map((badge) => badge.closest('div')?.querySelector('.mono')?.textContent);

  it('marks the default model under the default provider, whether or not Ollama tags the name', async () => {
    render(<ModelsView />);
    await screen.findByText('embeddinggemma:latest');

    expect(badged()).toEqual(['nomic-embed-text:latest']);
  });

  it('matches the default model whatever the case of its tag, as the server does', async () => {
    listEmbeddingModels.mockImplementation(async (provider: string) => ({
      provider,
      managed: true,
      configured: 'nomic-embed-text',
      models: [model('Nomic-Embed-Text:LATEST'), model('embeddinggemma:latest')],
      note: null,
    }));

    render(<ModelsView />);
    await screen.findByText('embeddinggemma:latest');

    expect(badged()).toEqual(['Nomic-Embed-Text:LATEST']);
  });

  it('does not mark a model of the same name under a provider that is not the default', async () => {
    const user = userEvent.setup();
    render(<ModelsView />);
    await screen.findByText('embeddinggemma:latest');

    await user.click(screen.getByRole('radio', { name: /openai/ }));
    await screen.findByText('text-embedding-3-small');

    expect(screen.getByText('nomic-embed-text')).toBeInTheDocument();
    expect(screen.queryByText('default for new corpora')).not.toBeInTheDocument();
  });

  it('marks it again on returning to the default provider', async () => {
    const user = userEvent.setup();
    render(<ModelsView />);
    await screen.findByText('embeddinggemma:latest');
    await user.click(screen.getByRole('radio', { name: /openai/ }));
    await screen.findByText('text-embedding-3-small');

    await user.click(screen.getByRole('radio', { name: /ollama/ }));
    await screen.findByText('embeddinggemma:latest');

    expect(badged()).toEqual(['nomic-embed-text:latest']);
  });
});
