import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CorpusDetail } from './App';
import type { Corpus } from './api';

/**
 * A corpus and the places it takes content from.
 *
 * docs/08 has described a per-source `.gitignore` toggle, include/exclude globs and a size
 * cap since the beginning. The API has accepted all of it since the beginning. The UI
 * never called `addSource` at all — so a corpus was stuck with the single source it was
 * created with, and the filters could be set over the API and then never seen again,
 * because the summary did not return them either.
 */
const getCorpus = vi.fn();
const listFiles = vi.fn();
const addSource = vi.fn();
const browse = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: {
    getCorpus: (...a: unknown[]) => getCorpus(...a),
    listFiles: (...a: unknown[]) => listFiles(...a),
    addSource: (...a: unknown[]) => addSource(...a),
    browse: (...a: unknown[]) => browse(...a),
    reindex: vi.fn(),
    updateCorpus: vi.fn(),
    deleteCorpus: vi.fn(),
  },
}));

function source(over: Partial<Corpus['sources'][number]> = {}): Corpus['sources'][number] {
  return {
    id: 's1',
    kind: 'workspace',
    rootPath: 'api-repo',
    useGitignore: true,
    maxFileBytes: 2 * 1024 * 1024,
    includeGlobs: [],
    excludeGlobs: [],
    ...over,
  };
}

function corpus(over: Partial<Corpus> = {}): Corpus {
  return {
    id: 'c1',
    name: 'docs',
    description: null,
    tenantId: 'default',
    owned: true,
    visibility: 'private',
    state: 'ready',
    createdUtc: new Date().toISOString(),
    lastIndexedUtc: new Date().toISOString(),
    sourceCount: 1,
    fileCount: 12,
    chunkCount: 114,
    skippedCount: 0,
    failedCount: 0,
    sources: [source()],
    chunkSets: [],
    ...over,
  };
}

const props = {
  name: 'docs',
  live: {},
  onBack: vi.fn(),
  onRefresh: async () => {},
  onError: vi.fn(),
};

beforeEach(() => {
  vi.clearAllMocks();
  getCorpus.mockResolvedValue(corpus());
  listFiles.mockResolvedValue({ files: [] });
  browse.mockResolvedValue({
    entries: [
      { name: 'api-repo', relativePath: 'api-repo', isDirectory: true, childCount: 4 },
      { name: 'notes', relativePath: 'notes', isDirectory: true, childCount: 2 },
      { name: 'README.md', relativePath: 'README.md', isDirectory: false, childCount: null },
    ],
  });
});

describe('the sources a corpus reads', () => {
  it('says what each source is actually doing, not just where it is', async () => {
    // Settable and invisible is the worst of both: the globs went into the database and
    // nothing could read them back out.
    getCorpus.mockResolvedValue(
      corpus({
        sources: [source({ includeGlobs: ['src/**'], excludeGlobs: ['**/vendor/**'] })],
      }),
    );

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText('api-repo')).toBeInTheDocument();
    expect(screen.getByText(/\.gitignore honoured/)).toBeInTheDocument();
    expect(screen.getByText(/only src\/\*\*/)).toBeInTheDocument();
    expect(screen.getByText(/not \*\*\/vendor\/\*\*/)).toBeInTheDocument();
  });

  it('says so when .gitignore is being ignored', async () => {
    // The difference between indexing a repo and indexing its node_modules.
    getCorpus.mockResolvedValue(corpus({ sources: [source({ useGitignore: false })] }));

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText(/\.gitignore ignored/)).toBeInTheDocument();
  });

  it('survives a server that does not send the globs at all', async () => {
    // The dev loop runs this UI against whatever container is up, which can be older than
    // the code. The first run of this screen against one blanked the page on
    // `undefined.length` — a whole screen lost to a field that had not shipped yet.
    getCorpus.mockResolvedValue(
      corpus({
        sources: [
          { id: 's1', kind: 'workspace', rootPath: 'api-repo', useGitignore: true, maxFileBytes: 1024 },
        ] as unknown as Corpus['sources'],
      }),
    );

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText('api-repo')).toBeInTheDocument();
    expect(screen.getByText(/\.gitignore honoured/)).toBeInTheDocument();
  });

  it('tells a corpus with no sources what to do about it', async () => {
    getCorpus.mockResolvedValue(corpus({ sources: [] }));

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText(/none — add one, or upload documents/)).toBeInTheDocument();
  });

  it('offers no add button on a corpus you do not own', async () => {
    // Shared grants read access; writes are always owner-only, and a button that 403s is
    // worse than no button.
    getCorpus.mockResolvedValue(corpus({ owned: false }));

    render(<CorpusDetail {...props} />);

    await screen.findByText('api-repo');
    expect(screen.queryByRole('button', { name: /add source/i })).not.toBeInTheDocument();
  });
});

describe('adding a source', () => {
  async function openAddSource() {
    const user = userEvent.setup();
    render(<CorpusDetail {...props} />);

    await user.click(await screen.findByRole('button', { name: /add source/i }));
    return { user, dialog: await screen.findByRole('dialog') };
  }

  it('offers only folders that are really mounted', async () => {
    // A path you can type is a path you can get wrong; a file is not a source.
    const { user, dialog } = await openAddSource();

    await user.click(within(dialog).getByLabelText(/Workspace folder/));
    const options = (await screen.findAllByRole('option')).map((o) => o.textContent);

    expect(options).toEqual(['(choose a folder)', 'api-repo', 'notes']);
  });

  it('will not submit without a folder', async () => {
    const { dialog } = await openAddSource();

    expect(within(dialog).getByRole('button', { name: /^Add source$/ })).toBeDisabled();
  });

  it('warns before indexing the same folder twice', async () => {
    const { user, dialog } = await openAddSource();

    await user.click(within(dialog).getByLabelText(/Workspace folder/));
    await user.click(await screen.findByRole('option', { name: 'api-repo' }));

    expect(within(dialog).getByText(/already indexes that folder/i)).toBeInTheDocument();
  });

  it('sends the filters, as a list and in bytes', async () => {
    // Globs are typed as a comma separated line and sent as an array; the cap is shown in
    // MB and sent in bytes. Both conversions are places to be quietly wrong.
    addSource.mockResolvedValue({});
    const { user, dialog } = await openAddSource();

    await user.click(within(dialog).getByLabelText(/Workspace folder/));
    await user.click(await screen.findByRole('option', { name: 'notes' }));

    await user.type(within(dialog).getByLabelText(/Only these/), 'src/**, docs/**');
    await user.type(within(dialog).getByLabelText(/Never these/), '**/vendor/**');

    await user.click(within(dialog).getByRole('button', { name: /^Add source$/ }));

    await waitFor(() => expect(addSource).toHaveBeenCalled());
    expect(addSource).toHaveBeenCalledWith('docs', {
      workspacePath: 'notes',
      useGitignore: true,
      maxFileBytes: 2 * 1024 * 1024,
      includeGlobs: ['src/**', 'docs/**'],
      excludeGlobs: ['**/vendor/**'],
    });
  });

  it('sends empty glob lists rather than a list containing nothing', async () => {
    // `''.split(',')` is `['']`, and a glob that matches nothing would exclude everything.
    addSource.mockResolvedValue({});
    const { user, dialog } = await openAddSource();

    await user.click(within(dialog).getByLabelText(/Workspace folder/));
    await user.click(await screen.findByRole('option', { name: 'notes' }));
    await user.click(within(dialog).getByRole('button', { name: /^Add source$/ }));

    await waitFor(() => expect(addSource).toHaveBeenCalled());
    expect(addSource.mock.calls[0][1]).toMatchObject({ includeGlobs: [], excludeGlobs: [] });
  });
});
