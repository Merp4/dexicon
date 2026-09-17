import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CorporaView } from './App';
import type { EmbeddingModelInfo } from './api';

/**
 * Creating a corpus, and choosing what will embed it.
 *
 * The form had no model picker at all: it sent a name, a description and a path, so every
 * corpus made in this UI silently took whatever the server was configured with. That is
 * the one property of a corpus that cannot be edited afterwards — a different model is a
 * different vector space — so getting it by default and discovering it later meant
 * building a second chunk set and promoting it.
 */
const createCorpus = vi.fn();
const listEmbeddingModels = vi.fn();
const browse = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: {
    createCorpus: (...a: unknown[]) => createCorpus(...a),
    listEmbeddingModels: (...a: unknown[]) => listEmbeddingModels(...a),
    browse: (...a: unknown[]) => browse(...a),
  },
}));

function model(name: string, measured: EmbeddingModelInfo['measured'] = null): EmbeddingModelInfo {
  return {
    name,
    sizeBytes: 622_000_000,
    dimensions: 768,
    inUse: false,
    documentTemplate: '{text}',
    queryTemplate: '{text}',
    templateOrigin: 'none',
    measured,
  };
}

const props = { corpora: [], live: {}, onRefresh: async () => {}, onOpen: vi.fn(), onError: vi.fn() };

beforeEach(() => {
  vi.clearAllMocks();
  createCorpus.mockResolvedValue({});
  browse.mockResolvedValue({ entries: [{ name: 'src', relativePath: 'src', isDirectory: true }] });
  listEmbeddingModels.mockResolvedValue({
    provider: 'ollama',
    managed: true,
    configured: 'embeddinggemma',
    note: null,
    models: [
      model('embeddinggemma:latest'),
      model('mxbai-embed-large:latest', {
        maxInputChars: 2816, truncatesSilently: true, recommendedChunkTokens: 665,
        charsPerToken: 2.82, measuredUtc: new Date().toISOString(),
      }),
    ],
  });
});

async function openCreate() {
  const user = userEvent.setup();
  render(<CorporaView {...props} />);

  await user.click(screen.getByRole('button', { name: /new corpus/i }));
  const dialog = await screen.findByRole('dialog');
  await waitFor(() => expect(listEmbeddingModels).toHaveBeenCalled());
  return { user, dialog };
}

describe('creating a corpus', () => {
  it('offers a model rather than silently using the configured one', async () => {
    const { dialog } = await openCreate();

    expect(within(dialog).getByLabelText('Embedding model')).toBeInTheDocument();
  });

  it('starts on the server’s configured default, matched past the :latest tag', async () => {
    // The server says `embeddinggemma`; the provider lists `embeddinggemma:latest`.
    // Comparing them raw would leave the field on whatever happened to be first.
    const { dialog } = await openCreate();

    expect(within(dialog).getByLabelText('Embedding model'))
      .toHaveTextContent('embeddinggemma:latest');
  });

  it('sends the chosen model', async () => {
    const { user, dialog } = await openCreate();

    await user.type(within(dialog).getByLabelText(/^Name/), 'api-repo');
    await user.click(within(dialog).getByLabelText('Embedding model'));
    await user.click(await screen.findByRole('option', { name: /mxbai/ }));
    await user.click(within(dialog).getByRole('button', { name: /^Create$/ }));

    await waitFor(() => expect(createCorpus).toHaveBeenCalled());
    expect(createCorpus.mock.calls[0][0]).toMatchObject({
      name: 'api-repo',
      embeddingModel: 'mxbai-embed-large:latest',
    });
  });

  it('sizes the first chunk set to what that model was measured at', async () => {
    // mxbai accepts 2,816 characters. Born at the default 768 tokens it would truncate
    // every full chunk from the first index, in the one setting that cannot be edited.
    const { user, dialog } = await openCreate();

    await user.type(within(dialog).getByLabelText(/^Name/), 'api-repo');
    await user.click(within(dialog).getByLabelText('Embedding model'));
    await user.click(await screen.findByRole('option', { name: /mxbai/ }));
    await user.click(within(dialog).getByRole('button', { name: /^Create$/ }));

    await waitFor(() => expect(createCorpus).toHaveBeenCalled());
    expect(createCorpus.mock.calls[0][0]).toMatchObject({ chunkSize: 665, chunkOverlap: 83 });
  });

  it('sends no chunk size for a model nobody has measured', async () => {
    // A guess pinned into an uneditable setting is worse than the server's own default.
    const { user, dialog } = await openCreate();

    await user.type(within(dialog).getByLabelText(/^Name/), 'api-repo');
    await user.click(within(dialog).getByRole('button', { name: /^Create$/ }));

    await waitFor(() => expect(createCorpus).toHaveBeenCalled());
    expect(createCorpus.mock.calls[0][0].chunkSize).toBeUndefined();
  });

  it('says the model cannot be changed later, because it cannot', async () => {
    const { dialog } = await openCreate();

    expect(within(dialog).getByText(/different model is a different vector space/i))
      .toBeInTheDocument();
  });
});
