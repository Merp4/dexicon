import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CorpusDetail } from './App';
import { ApiError, type Corpus } from './api';

/**
 * A corpus and the places it takes content from.
 *
 * docs/08 has described a per-source `.gitignore` toggle, include/exclude globs and a size
 * cap since the beginning. The API has accepted all of it since the beginning. The UI
 * never called `addSource` at all, so a corpus was stuck with the single source it was
 * created with, and the filters could be set over the API and then never seen again,
 * because the summary did not return them either.
 */
const getCorpus = vi.fn();
const listFiles = vi.fn();
const addSource = vi.fn();
const browse = vi.fn();
const removeSource = vi.fn();
const coverage = vi.fn();
const updateSource = vi.fn();
const updateCorpus = vi.fn();
const reindex = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: {
    getCorpus: (...a: unknown[]) => getCorpus(...a),
    listFiles: (...a: unknown[]) => listFiles(...a),
    addSource: (...a: unknown[]) => addSource(...a),
    browse: (...a: unknown[]) => browse(...a),
    removeSource: (...a: unknown[]) => removeSource(...a),
    coverage: (...a: unknown[]) => coverage(...a),
    updateSource: (...a: unknown[]) => updateSource(...a),
    updateCorpus: (...a: unknown[]) => updateCorpus(...a),
    reindex: (...a: unknown[]) => reindex(...a),
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
    fileCount: 12,
    ...over,
  };
}

function chunkSet(over: Partial<Corpus['chunkSets'][number]> = {}): Corpus['chunkSets'][number] {
  return {
    id: 'cs1',
    name: 'default',
    description: null,
    embeddingProvider: 'ollama',
    embeddingModel: 'nomic-embed-text',
    embeddingDimensions: 768,
    collectionName: 'dexicon_768',
    chunkSize: 768,
    chunkOverlap: 100,
    boundaryMode: 'paragraph',
    customBoundaryPattern: null,
    unitAware: true,
    sentenceAware: true,
    headingContext: true,
    isDefault: true,
    state: 'ready',
    fileCount: 12,
    chunkCount: 114,
    pendingCount: 0,
    failedCount: 0,
    createdUtc: new Date().toISOString(),
    lastIndexedUtc: new Date().toISOString(),
    ...over,
  } as Corpus['chunkSets'][number];
}

function corpus(over: Partial<Corpus> = {}): Corpus {
  return {
    id: 'c1',
    name: 'docs',
    description: null,
    state: 'ready',
    createdUtc: new Date().toISOString(),
    lastIndexedUtc: new Date().toISOString(),
    sourceCount: 1,
    fileCount: 12,
    chunkCount: 114,
    skippedCount: 0,
    pendingCount: 0,
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
  coverage.mockResolvedValue({ gaps: [] });
  updateSource.mockResolvedValue({});
  updateCorpus.mockResolvedValue({});
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
    // `undefined.length`: a whole screen lost to a field that had not shipped yet.
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

  /**
   * A history source ignores the size cap and .gitignore, and what it counts is commits.
   * Rendering the file settings against one said it obeyed three things it does not
   * read, and called its commits files.
   */
  it('describes a history source by what it actually does', async () => {
    getCorpus.mockResolvedValue(
      corpus({
        sources: [
          // Not the corpus's own total, which is rendered elsewhere on the page.
          source({ id: 's1', kind: 'workspace', rootPath: 'api-repo', fileCount: 37 }),
          source({
            id: 's2', kind: 'githistory', rootPath: 'api-repo', fileCount: 201,
            git: { ref: 'main', includeMessage: true, includeStat: true, includeDiff: true, maxDiffBytes: 65536, includeMerges: false },
          }),
        ],
      }),
    );

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText(/201 commits/)).toBeInTheDocument();
    expect(screen.getByText(/37 files/)).toBeInTheDocument();
    expect(screen.getByText(/main.*message, stat and diff/)).toBeInTheDocument();

    // One .gitignore line, for the workspace source, and none for the history one.
    expect(screen.getAllByText(/\.gitignore honoured/)).toHaveLength(1);
  });

  /**
   * The ref names what to follow and says nothing about whether it moves. A source over a
   * local branch nobody pulled indexed the same commits for three days, and every count
   * on this screen was correct.
   */
  it('says how old the newest commit of a history source is', async () => {
    const threeDaysAgo = new Date(Date.now() - 3 * 86_400_000 - 60_000).toISOString();
    getCorpus.mockResolvedValue(
      corpus({
        sources: [
          source({
            kind: 'githistory', rootPath: 'api-repo', fileCount: 174,
            git: { ref: 'HEAD', includeMessage: true, includeStat: true, includeDiff: false, maxDiffBytes: 65536, includeMerges: false },
            newestCommit: { sha: '9d2ef0633a530462b4091ad8226d1b02faadea84', authoredUtc: threeDaysAgo },
          }),
        ],
      }),
    );

    render(<CorpusDetail {...props} />);

    // What a sighted reader sees at a glance, hidden from a screen reader so the
    // sentence below is not read after it.
    const shown = await screen.findByText(/newest/, { selector: '[aria-hidden="true"]' });
    expect(shown).toHaveTextContent('newest 9d2ef06, 3d ago');
    expect(shown.querySelector('time')).toHaveAttribute('dateTime', threeDaysAgo);

    // The whole sha and the exact time, for comparing with git: in the title for a
    // pointer, and in text a screen reader reads, since a title is not reliably read.
    const spoken = screen.getByText(/newest commit/, { selector: '.sr-only' });
    expect(spoken).toHaveTextContent('newest commit 9d2ef0633a530462b4091ad8226d1b02faadea84');
    expect(spoken).toHaveTextContent('3d ago');
    expect(shown.parentElement).toHaveAttribute('title', expect.stringContaining('9d2ef0633a530462b4091ad8226d1b02faadea84'));
  });

  it('says nothing about a newest commit before one was found', async () => {
    getCorpus.mockResolvedValue(
      corpus({
        sources: [
          source({
            kind: 'githistory', rootPath: 'api-repo', fileCount: 0,
            git: { ref: 'HEAD', includeMessage: true, includeStat: true, includeDiff: false, maxDiffBytes: 65536, includeMerges: false },
            newestCommit: null,
          }),
        ],
      }),
    );

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText(/message and stat/)).toBeInTheDocument();
    expect(screen.queryByText(/newest/)).not.toBeInTheDocument();
  });

  /**
   * A source at the workspace root has an empty path. It rendered with no name at all,
   * and its buttons were labelled "Remove source " with nothing after it.
   */
  it('names a source at the workspace root', async () => {
    getCorpus.mockResolvedValue(corpus({ sources: [source({ rootPath: '' })] }));

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText('workspace root')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Remove source workspace root' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Edit filters for workspace root' })).toBeInTheDocument();
  });

  /**
   * What each commit holds, from all three settings. It was read off the diff alone, so
   * a source with the message turned off still said "message and stat".
   */
  it('says what each commit holds from all three settings', async () => {
    getCorpus.mockResolvedValue(
      corpus({
        sources: [
          source({
            id: 's2', kind: 'githistory', rootPath: 'api-repo', fileCount: 201,
            git: { ref: 'main', includeMessage: false, includeStat: true, includeDiff: false, maxDiffBytes: 65536, includeMerges: false },
          }),
        ],
      }),
    );

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText(/main · stat only/)).toBeInTheDocument();
    expect(screen.queryByText(/message and stat/)).not.toBeInTheDocument();
  });

  /**
   * The corpus total counts both kinds, so neither "files" nor "commits" is true of it.
   * Saying "files" contradicted the source row directly beneath it.
   */
  it('counts a mixed corpus in documents and a history-only one in commits', async () => {
    getCorpus.mockResolvedValue(
      corpus({
        fileCount: 238,
        sources: [
          source({ id: 's1', kind: 'workspace' }),
          source({ id: 's2', kind: 'githistory' }),
        ],
      }),
    );

    // The number and the unit are separate JSX children, so this reads the rendered
    // text rather than one node: a matcher that only sees one node would report a
    // failure that is about the markup and not about the label.
    const says = (text: string) => (_: string, el: Element | null) =>
      (el?.textContent ?? '').replace(/\s+/g, ' ').includes(text);

    const { unmount } = render(<CorpusDetail {...props} />);
    expect(await screen.findAllByText(says('238 documents'))).not.toHaveLength(0);
    unmount();

    getCorpus.mockResolvedValue(
      corpus({ fileCount: 201, sources: [source({ id: 's2', kind: 'githistory' })] }),
    );

    render(<CorpusDetail {...props} />);
    expect(await screen.findAllByText(says('201 commits'))).not.toHaveLength(0);
  });

  it('tells a corpus with no sources what to do about it', async () => {
    getCorpus.mockResolvedValue(corpus({ sources: [] }));

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText(/none: add one, or upload documents/)).toBeInTheDocument();
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
    const { dialog } = await openAddSource();

    await within(dialog).findByRole('button', { name: /api-repo/ });
    expect(within(dialog).getByRole('button', { name: /notes/ })).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: /README/ })).not.toBeInTheDocument();
  });

  it('can reach a folder that is not at the top level', async () => {
    // The whole reason this stopped being a flat list: `GET /api/workspaces` has always
    // taken a path and the UI never passed one, so a corpus could only ever be pointed at
    // a top-level directory. A shelf of books at books/manuals/Architecture was unreachable.
    addSource.mockResolvedValue({});
    const { user, dialog } = await openAddSource();

    browse.mockResolvedValueOnce({
      entries: [{ name: 'manuals', relativePath: 'notes/manuals', isDirectory: true, childCount: 10 }],
    });
    await user.click(await within(dialog).findByRole('button', { name: /notes/ }));

    await waitFor(() => expect(browse).toHaveBeenLastCalledWith('notes'));
    await user.click(await within(dialog).findByRole('button', { name: /manuals/ }));
    await user.click(within(dialog).getByRole('button', { name: /^Add source$/ }));

    await waitFor(() => expect(addSource).toHaveBeenCalled());
    expect(addSource.mock.calls[0][1]).toMatchObject({ workspacePath: 'notes/manuals' });
  });

  it('will not submit without a folder', async () => {
    const { dialog } = await openAddSource();

    expect(within(dialog).getByRole('button', { name: /^Add source$/ })).toBeDisabled();
  });

  /**
   * The root is a choice like any other, made with its breadcrumb. A file source there
   * takes everything no deeper source claims, so the form says so and the button names it.
   */
  it('can choose the workspace root, and says what a file source there takes', async () => {
    addSource.mockResolvedValue({});
    const { user, dialog } = await openAddSource();

    await user.click(within(dialog).getByRole('button', { name: '(choose a folder)' }));

    expect(within(dialog).getByText(/Indexing the workspace root and everything beneath it/)).toBeInTheDocument();
    expect(within(dialog).getByText(/takes everything under it that no other source/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: /^Add the workspace root$/ }));

    await waitFor(() => expect(addSource).toHaveBeenCalled());
    expect(addSource.mock.calls[0][1]).toMatchObject({ workspacePath: '' });
  });

  it('does not warn about a history source at the root, which is the repository only', async () => {
    const { user, dialog } = await openAddSource();

    await user.click(within(dialog).getByRole('button', { name: '(choose a folder)' }));
    await user.click(within(dialog).getByRole('checkbox', { name: /Index its commit history/ }));

    expect(within(dialog).queryByText(/takes everything under it/)).not.toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: /^Add source$/ })).toBeEnabled();
  });

  it('warns before indexing the same folder twice', async () => {
    const { user, dialog } = await openAddSource();

    await user.click(await within(dialog).findByRole('button', { name: /api-repo/ }));

    expect(within(dialog).getByText(/already indexes that folder/i)).toBeInTheDocument();
  });

  /**
   * Indexing a repository's files and its history is deliberately two sources over one
   * root. Warning on the path alone told the reader that the thing the feature exists
   * for was a mistake.
   */
  it('does not call the history source a duplicate of the file source', async () => {
    const { user, dialog } = await openAddSource();

    await user.click(await within(dialog).findByRole('button', { name: /api-repo/ }));
    expect(within(dialog).getByText(/already indexes that folder/i)).toBeInTheDocument();

    await user.click(within(dialog).getByRole('checkbox', { name: /Index its commit history/ }));

    expect(within(dialog).queryByText(/already indexes that folder/i)).not.toBeInTheDocument();
  });

  it('still warns about a second history source on one folder', async () => {
    getCorpus.mockResolvedValue(
      corpus({ sources: [source({ kind: 'githistory', rootPath: 'api-repo' })] }),
    );
    const { user, dialog } = await openAddSource();

    await user.click(await within(dialog).findByRole('button', { name: /api-repo/ }));
    expect(within(dialog).queryByText(/already indexes/i)).not.toBeInTheDocument();

    await user.click(within(dialog).getByRole('checkbox', { name: /Index its commit history/ }));

    expect(within(dialog).getByText(/already indexes that folder’s history/i)).toBeInTheDocument();
  });

  /**
   * A history source indexes commits, so the settings that describe files do not apply
   * to it. Leaving them on screen would offer a size cap and a .gitignore toggle for
   * work that reads neither, and sending them would leave a source whose displayed
   * filters describe something it does not do.
   */
  it('asks about commits instead of files when the history is wanted', async () => {
    addSource.mockResolvedValue({});
    const { user, dialog } = await openAddSource();

    await user.click(await within(dialog).findByRole('button', { name: /api-repo/ }));

    expect(within(dialog).getByLabelText(/Largest file/)).toBeInTheDocument();

    await user.click(within(dialog).getByRole('checkbox', { name: /Index its commit history/ }));

    expect(within(dialog).queryByLabelText(/Largest file/)).not.toBeInTheDocument();
    expect(within(dialog).queryByText(/Honour \.gitignore/)).not.toBeInTheDocument();
    expect(within(dialog).getByText(/Include the diff/)).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: /^Add source$/ }));

    await waitFor(() => expect(addSource).toHaveBeenCalled());
    expect(addSource.mock.calls[0][1]).toMatchObject({ gitHistory: true, git: { includeDiff: false } });
    expect(addSource.mock.calls[0][1].maxFileBytes).toBeUndefined();
    expect(addSource.mock.calls[0][1].useGitignore).toBeUndefined();
  });

  it('sends the diff setting when it is asked for', async () => {
    addSource.mockResolvedValue({});
    const { user, dialog } = await openAddSource();

    await user.click(await within(dialog).findByRole('button', { name: /api-repo/ }));
    await user.click(within(dialog).getByRole('checkbox', { name: /Index its commit history/ }));
    await user.click(within(dialog).getByRole('checkbox', { name: /Include the diff/ }));
    await user.click(within(dialog).getByRole('button', { name: /^Add source$/ }));

    await waitFor(() => expect(addSource).toHaveBeenCalled());
    expect(addSource.mock.calls[0][1]).toMatchObject({ git: { includeDiff: true } });
  });

  /**
   * The add and edit dialogs share one set of history fields. A source over a checkout's
   * HEAD follows a local branch that moves only when someone pulls; choosing the ref when
   * the source is added is how that is avoided rather than repaired.
   */
  it('lets the ref be chosen when the history is added', async () => {
    addSource.mockResolvedValue({});
    const { user, dialog } = await openAddSource();

    await user.click(await within(dialog).findByRole('button', { name: /api-repo/ }));
    await user.click(within(dialog).getByRole('checkbox', { name: /Index its commit history/ }));

    const ref = within(dialog).getByLabelText('Ref');
    await user.clear(ref);
    await user.type(ref, 'origin/main');
    await user.click(within(dialog).getByRole('button', { name: /^Add source$/ }));

    await waitFor(() => expect(addSource).toHaveBeenCalled());
    expect(addSource.mock.calls[0][1]).toMatchObject({ git: { ref: 'origin/main', includeMessage: true } });
  });

  /**
   * A refusal belongs where the settings it names still are. Sent to the page, it landed
   * behind the dialog, which stayed open saying nothing.
   */
  it('shows a refusal in the dialog, which stays open', async () => {
    addSource.mockRejectedValue(new ApiError(400, 'Unusable history settings',
      "'main..other' is not a usable ref. A branch, a tag or an object name."));
    const { user, dialog } = await openAddSource();

    await user.click(await within(dialog).findByRole('button', { name: /api-repo/ }));
    await user.click(within(dialog).getByRole('checkbox', { name: /Index its commit history/ }));
    const ref = within(dialog).getByLabelText('Ref');
    await user.clear(ref);
    await user.type(ref, 'main..other');
    await user.click(within(dialog).getByRole('button', { name: /^Add source$/ }));

    expect(await within(dialog).findByText(/is not a usable ref/)).toBeInTheDocument();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(props.onError).not.toHaveBeenCalled();
  });

  it('sends the filters, as a list and in bytes', async () => {
    // Globs are typed as a comma separated line and sent as an array; the cap is shown in
    // MB and sent in bytes. Both conversions are places to be quietly wrong.
    addSource.mockResolvedValue({});
    const { user, dialog } = await openAddSource();

    await user.click(await within(dialog).findByRole('button', { name: /notes/ }));

    await user.type(within(dialog).getByLabelText(/Only these/), 'src/**, docs/**');
    await user.type(within(dialog).getByLabelText(/Never these/), '**/vendor/**');

    await user.click(within(dialog).getByRole('button', { name: /^Add source$/ }));

    await waitFor(() => expect(addSource).toHaveBeenCalled());
    expect(addSource).toHaveBeenCalledWith('docs', {
      workspacePath: 'notes',
      useGitignore: true,
      maxFileBytes: 20 * 1024 * 1024,
      includeGlobs: ['src/**', 'docs/**'],
      excludeGlobs: ['**/vendor/**'],
    });
  });

  it('omits a glob nobody typed rather than sending one that matches nothing', async () => {
    // Two failures guarded here. `''.split(',')` is `['']`, and a glob matching nothing
    // would exclude everything. And an empty ARRAY is not nothing either: it means "no
    // globs, whatever the corpus default says", so sending it for an untouched box would
    // pin every new source against the default it was supposed to follow. Omitted is the
    // only one of the three that means "follow the corpus".
    addSource.mockResolvedValue({});
    const { user, dialog } = await openAddSource();

    await user.click(await within(dialog).findByRole('button', { name: /notes/ }));
    await user.click(within(dialog).getByRole('button', { name: /^Add source$/ }));

    await waitFor(() => expect(addSource).toHaveBeenCalled());
    expect(addSource.mock.calls[0][1].includeGlobs).toBeUndefined();
    expect(addSource.mock.calls[0][1].excludeGlobs).toBeUndefined();
  });
});

describe('a count taken while the walk is running', () => {
  // A source added a moment ago sat at "no files" in the warning colour, beside a corpus
  // badge reading "indexing". A folder that has not been walked yet and one that a
  // mistyped path made empty are different problems, and they read identically.
  const twoSources = (over = {}) =>
    corpus({
      sources: [
        source({ id: 's1', rootPath: 'books/manuals', fileCount: 96 }),
        source({ id: 's2', rootPath: 'books/Dev', fileCount: 0 }),
      ],
      ...over,
    });

  it('does not call a source empty while the corpus is still indexing', async () => {
    getCorpus.mockResolvedValue(twoSources({ state: 'indexing' }));

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText('counting…')).toBeInTheDocument();
    expect(screen.queryByText('no files')).not.toBeInTheDocument();
  });

  it('marks a partial tally as partial rather than as a total', async () => {
    getCorpus.mockResolvedValue(twoSources({ state: 'indexing' }));

    render(<CorpusDetail {...props} />);

    // 96 is what has been counted so far, not what the folder holds. The unit is there
    // for the same reason it is on every other count, and "so far" is what stops it
    // reading as a total — so the assertion below is the one that matters.
    expect(await screen.findByText('96 files so far')).toBeInTheDocument();
    expect(screen.queryByText('96 files')).not.toBeInTheDocument();
  });

  it('takes a running job as indexing even before the corpus state catches up', async () => {
    // The stream knows within a second of a refresh starting; corpus.state is whatever the
    // last load returned, which on a fresh page is already stale.
    getCorpus.mockResolvedValue(twoSources({ state: 'ready' }));
    const live = { c1: { id: 'j1', corpusId: 'c1', state: 'running' } };

    render(<CorpusDetail {...props} live={live as never} />);

    expect(await screen.findByText('counting…')).toBeInTheDocument();
  });

  it('still says a folder brought in nothing once the walk has finished', async () => {
    // The warning this replaced is worth keeping: with the run over, an empty source is a
    // finding, and it is the only place a mistyped path shows up.
    getCorpus.mockResolvedValue(twoSources({ state: 'ready' }));

    render(<CorpusDetail {...props} />);

    const empty = await screen.findByText('no files');
    expect(empty.className).toMatch(/warn/);
    expect(screen.getByText('96 files')).toBeInTheDocument();
  });
});

describe('what each source contributed', () => {
  it('says nothing about it when there is only one source', async () => {
    // The corpus total IS the source total. Repeating it on the row is noise.
    getCorpus.mockResolvedValue(corpus({ sources: [source({ fileCount: 12 })] }));
    render(<CorpusDetail {...props} />);

    await screen.findByText('api-repo');
    expect(screen.queryByText('12 files')).not.toBeInTheDocument();
  });

  it('shows a count per source once there are several', async () => {
    getCorpus.mockResolvedValue(corpus({
      sources: [
        source({ id: 's1', rootPath: 'manuals/AI', fileCount: 34 }),
        source({ id: 's2', rootPath: 'manuals/Philosophy', fileCount: 12 }),
      ],
    }));
    render(<CorpusDetail {...props} />);

    expect(await screen.findByText('34 files')).toBeInTheDocument();
    expect(screen.getByText('12 files')).toBeInTheDocument();
  });

  it('calls out a source that brought in nothing', async () => {
    // A mistyped path, an over-eager exclude glob and an index that stopped early all
    // look identical from a corpus-level count: fine. This is the only place it shows.
    getCorpus.mockResolvedValue(corpus({
      sources: [
        source({ id: 's1', rootPath: 'manuals/AI', fileCount: 34 }),
        source({ id: 's2', rootPath: 'manuals/Typo', fileCount: 0 }),
      ],
    }));
    render(<CorpusDetail {...props} />);

    expect(await screen.findByText('no files')).toBeInTheDocument();
  });
});

describe('removing a source', () => {
  it('offers a remove control per source', async () => {
    getCorpus.mockResolvedValue(corpus({ sources: [source({ rootPath: 'manuals/AI' })] }));
    render(<CorpusDetail {...props} />);

    expect(await screen.findByRole('button', { name: /Remove source manuals\/AI/ })).toBeInTheDocument();
  });

  it('says what it costs before doing it', async () => {
    // Adding a folder is one click, so removing one should be too, but the cost has to
    // be stated, because the files leave every chunk set, not just the default one.
    const user = userEvent.setup();
    getCorpus.mockResolvedValue(corpus({ sources: [source({ rootPath: 'manuals/AI', fileCount: 34 })] }));
    render(<CorpusDetail {...props} />);

    await user.click(await screen.findByRole('button', { name: /Remove source manuals\/AI/ }));
    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).getByText(/34 files leave the index/)).toBeInTheDocument();
    expect(within(dialog).getByText(/folder on disk is untouched/)).toBeInTheDocument();
    expect(removeSource).not.toHaveBeenCalled();
  });

  it('removes it only once confirmed', async () => {
    const user = userEvent.setup();
    removeSource.mockResolvedValue(undefined);
    getCorpus.mockResolvedValue(corpus({ sources: [source({ id: 's9', rootPath: 'manuals/AI' })] }));
    render(<CorpusDetail {...props} />);

    await user.click(await screen.findByRole('button', { name: /Remove source manuals\/AI/ }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: /^Remove source$/ }));

    await waitFor(() => expect(removeSource).toHaveBeenCalledWith('docs', 's9'));
  });

  it('cancels without removing anything', async () => {
    const user = userEvent.setup();
    getCorpus.mockResolvedValue(corpus({ sources: [source({ rootPath: 'manuals/AI' })] }));
    render(<CorpusDetail {...props} />);

    await user.click(await screen.findByRole('button', { name: /Remove source manuals\/AI/ }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: /^Cancel$/ }));

    expect(removeSource).not.toHaveBeenCalled();
  });

  it('says why in the dialog when the server refuses', async () => {
    const user = userEvent.setup();
    removeSource.mockRejectedValue(new ApiError(409, 'Corpus busy', 'The corpus is being indexed. Try again when it finishes.'));
    getCorpus.mockResolvedValue(corpus({ sources: [source({ id: 's9', rootPath: 'manuals/AI' })] }));
    render(<CorpusDetail {...props} />);

    await user.click(await screen.findByRole('button', { name: /Remove source manuals\/AI/ }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: /^Remove source$/ }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(/being indexed/);
    expect(props.onError).not.toHaveBeenCalled();
  });
});

/**
 * Files the corpus has no row for.
 *
 * A file outside every source root is not skipped and not failed: it is absent. Nothing on
 * this page counted it, and a search for it returned other documents, which reads like a
 * ranking result rather than a gap. A real library indexed 95 of 138 files this way.
 *
 * The notice is only worth having if it stays quiet, so most of these are about when it
 * does not appear.
 */
describe('finding one file among many', () => {
  const file = (relativePath: string, id: string) => ({
    id,
    relativePath,
    status: 'indexed',
    chunkCount: 40,
    sizeBytes: 1024,
    statusDetail: null,
  });

  const shelf = [
    file('A Made-Up Manual of Imaginary Machines, 2nd Edition.epub', 'f1'),
    file('A Made-Up Manual of Imaginary Machines, 2nd Edition.pdf', 'f2'),
    file('An Invented Handbook.pdf', 'f3'),
    file('src/Dexicon.Core/Auth/ScopeResolver.cs', 'f4'),
  ];

  // Answers the query rather than returning the shelf whatever is asked, because the
  // filtering is the server's now. A mock that ignores `name` would let a component
  // that never sends it pass.
  beforeEach(() => listFiles.mockImplementation((_: string, opts: { name?: string } = {}) => {
    const matched = opts.name
      ? shelf.filter((f) => f.relativePath.toLowerCase().includes(opts.name!.toLowerCase()))
      : shelf;
    return Promise.resolve({ total: matched.length, chunkSet: 'default', files: matched });
  }));

  it('narrows a shelf of books to the one being looked for', async () => {
    // Ninety-six titles, alphabetical, each present twice. The status tabs do not help
    // when every one of them is indexed and you want a particular book.
    const user = userEvent.setup();
    render(<CorpusDetail {...props} />);

    await user.type(await screen.findByLabelText('Filter files by name'), 'made-up');

    // Both inside the wait. The titles that survive the filter are on screen before the
    // refetch as well, so asserting them first and the absence afterwards passes while
    // the debounced query is still in flight.
    await waitFor(() => {
      expect(screen.getAllByRole('button', { name: /A Made-Up Manual/ })).toHaveLength(2);
      expect(screen.queryByRole('button', { name: /An Invented Handbook/ })).not.toBeInTheDocument();
    });
  });

  it('says the whole corpus was searched rather than looking like an empty one', async () => {
    const user = userEvent.setup();
    render(<CorpusDetail {...props} />);

    await user.type(await screen.findByLabelText('Filter files by name'), 'zzzz');

    expect(await screen.findByText('No file matches that')).toBeInTheDocument();
    expect(screen.getByText(/Searched all 12 files in this corpus/)).toBeInTheDocument();
  });

  it('sets a document title in the body face and a path in monospace', async () => {
    // "…Who Have Not Yet Invented Anything, 3rd Edition.epub" wrapped as "3rd Editio / n.epub":
    // break-all is right for a path and wrong for a sentence.
    render(<CorpusDetail {...props} />);

    const book = await screen.findByRole('button', { name: /An Invented Handbook/ });
    expect(book.className).toMatch(/break-words/);
    expect(book.className).not.toMatch(/\bmono\b/);

    const path = screen.getByRole('button', { name: /ScopeResolver\.cs/ });
    expect(path.className).toMatch(/\bmono\b/);
    expect(path.className).toMatch(/break-all/);
  });
});

describe('files no source covers', () => {
  const gap = (over: Partial<{ directory: string; files: string[] }> = {}) => ({
    directory: 'books/manuals',
    files: ['An Invented Handbook.pdf'],
    ...over,
  });

  it('names the directory, the file, and what it means for search', async () => {
    coverage.mockResolvedValue({ gaps: [gap()] });

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText(/covered by no source/)).toBeInTheDocument();
    expect(screen.getByText('books/manuals')).toBeInTheDocument();
    expect(screen.getByText('An Invented Handbook.pdf')).toBeInTheDocument();
    // The consequence, not just the fact. Without it this is a statistic.
    expect(screen.getByText(/Searching will never return it/)).toBeInTheDocument();
  });

  it('says nothing at all when every file is covered', async () => {
    // The ordinary case. A warning that appears when there is nothing to say is one that
    // gets ignored when there is.
    coverage.mockResolvedValue({ gaps: [] });

    render(<CorpusDetail {...props} />);

    await screen.findByText('api-repo');
    expect(screen.queryByText(/covered by no source/)).not.toBeInTheDocument();
  });

  it('lists five files and counts the rest', async () => {
    coverage.mockResolvedValue({
      gaps: [gap({ files: Array.from({ length: 12 }, (_, i) => `book-${i + 1}.pdf`) })],
    });

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText('book-5.pdf')).toBeInTheDocument();
    expect(screen.queryByText('book-6.pdf')).not.toBeInTheDocument();
    expect(screen.getByText(/and 7 more/)).toBeInTheDocument();
  });

  it('names the workspace root rather than leaving a blank', async () => {
    // The root is the empty string, and "files in  are covered by no source" is what that
    // renders as if it is passed straight through.
    coverage.mockResolvedValue({ gaps: [gap({ directory: '', files: ['README.md'] })] });

    render(<CorpusDetail {...props} />);

    // Named in the sentence and again on the button, which is why this asserts on both
    // rather than on a single match.
    expect(await screen.findByText('the workspace root')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: /Add a source on the workspace root/ }),
    ).toBeInTheDocument();
  });

  it('opens the add-source form already pointed at the gap', async () => {
    // The fix is one click from the warning. Retyping a path read off a notice is where
    // this goes wrong.
    const user = userEvent.setup();
    coverage.mockResolvedValue({ gaps: [gap()] });

    render(<CorpusDetail {...props} />);
    await user.click(await screen.findByRole('button', { name: /Add a source on books\/manuals/ }));

    // The picker shows the chosen folder rather than holding it in a text field, so the
    // assertion is on what the reader sees it is about to index.
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText('books/manuals')).toBeInTheDocument();
    expect(within(dialog).getByText(/Indexing/)).toBeInTheDocument();
  });

  /**
   * A gap at the workspace root opened the form with the root chosen, and the form could
   * not be sent: the root is the empty string, and the submit was disabled while the path
   * was falsy. The notice's own button led somewhere with no way forward.
   */
  it('adds a source on the workspace root from a gap there', async () => {
    const user = userEvent.setup();
    addSource.mockResolvedValue({});
    coverage.mockResolvedValue({ gaps: [gap({ directory: '', files: ['README.md'] })] });

    render(<CorpusDetail {...props} />);
    await user.click(await screen.findByRole('button', { name: /Add a source on the workspace root/ }));

    // The form's own submit, found by what it is rather than by its label.
    const dialog = await screen.findByRole('dialog');
    const submit = dialog.querySelector<HTMLButtonElement>('button[type="submit"]');
    expect(submit).not.toBeNull();
    await user.click(submit!);

    await waitFor(() => expect(addSource).toHaveBeenCalled());
    expect(addSource.mock.calls[0][1]).toMatchObject({ workspacePath: '' });
  });

  it('keeps the page when the endpoint is missing or fails', async () => {
    // The dev loop serves this UI against whatever container is up, which can predate the
    // endpoint entirely. A missing warning is not a broken page, and onError would put a
    // banner on the screen for something the reader can do nothing about.
    coverage.mockRejectedValue(new Error('404'));

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText('api-repo')).toBeInTheDocument();
    expect(screen.queryByText(/covered by no source/)).not.toBeInTheDocument();
    expect(props.onError).not.toHaveBeenCalled();
  });
});

/**
 * Changing a source's filters, and the corpus default they fall back to.
 *
 * Filters were write-once: set when the folder was added and unreachable afterwards, so
 * changing one meant deleting the source, which drops its files from every chunk set, and
 * re-embedding the folder from scratch.
 *
 * The distinction these protect is null against empty. A source with no opinion follows
 * the corpus; a source with an empty list has decided on none. The UI has to send those
 * two differently, or a reset silently re-pins whatever was on screen.
 */
describe('editing a source filter', () => {
  const owning = (over: Partial<Corpus['sources'][number]> = {}) =>
    corpus({
      defaults: {
        useGitignore: false,
        maxFileBytes: 64 * 1024 * 1024,
        includeGlobs: null,
        excludeGlobs: ['**/*.pdf'],
      },
      sources: [source({
        rootPath: 'books/manuals/AI',
        useGitignore: false,
        maxFileBytes: 64 * 1024 * 1024,
        excludeGlobs: ['**/*.pdf'],
        ownUseGitignore: null,
        ownMaxFileBytes: null,
        ownIncludeGlobs: null,
        ownExcludeGlobs: null,
        ...over,
      })] as Corpus['sources'],
    }) as Corpus;

  async function openEdit(c: Corpus) {
    const user = userEvent.setup();
    getCorpus.mockResolvedValue(c);
    render(<CorpusDetail {...props} />);
    await user.click(await screen.findByRole('button', { name: /Edit filters for books\/manuals\/AI/ }));
    return { user, dialog: await screen.findByRole('dialog') };
  }

  it('says which values are the corpus and not this source', async () => {
    // Without it a reader takes every value for one they typed here, and editing the
    // corpus default looks like it did nothing.
    getCorpus.mockResolvedValue(owning());
    render(<CorpusDetail {...props} />);

    expect(await screen.findByText(/some from the corpus/)).toBeInTheDocument();
  });

  it('says nothing about inheritance when the source sets everything itself', async () => {
    getCorpus.mockResolvedValue(owning({
      ownUseGitignore: false,
      ownMaxFileBytes: 64 * 1024 * 1024,
      ownIncludeGlobs: [],
      ownExcludeGlobs: ['**/*.pdf'],
    }));
    render(<CorpusDetail {...props} />);

    await screen.findByText('books/manuals/AI');
    expect(screen.queryByText(/some from the corpus/)).not.toBeInTheDocument();
  });

  it('says nothing when the corpus sets no defaults to inherit', async () => {
    // Found by looking at the real library: every source there inherits the ABSENCE of
    // globs, so an "inherited" word appeared on all ten rows and said nothing. A label on
    // every row is one a reader learns to skip.
    getCorpus.mockResolvedValue(corpus({
      sources: [source({ rootPath: 'books/manuals/AI' })],
    }));
    render(<CorpusDetail {...props} />);

    await screen.findByText('books/manuals/AI');
    expect(screen.queryByText(/from the corpus/)).not.toBeInTheDocument();
  });

  it('shows what each inherited field would actually be', async () => {
    // A checkbox saying only "use the corpus default" asks someone to accept a value they
    // cannot see, which is the one thing the control is for.
    const { dialog } = await openEdit(owning());

    expect(within(dialog).getByText(/Corpus default \(\.gitignore ignored\)/)).toBeInTheDocument();
    expect(within(dialog).getByText(/Corpus default \(\*\*\/\*\.pdf\)/)).toBeInTheDocument();
  });

  it('clears every field the source still inherits', async () => {
    const { user, dialog } = await openEdit(owning());

    await user.click(within(dialog).getByRole('button', { name: /^Save filters$/ }));

    await waitFor(() => expect(updateSource).toHaveBeenCalled());
    const body = updateSource.mock.calls[0][2];
    expect(body.clear).toEqual(
      expect.arrayContaining(['useGitignore', 'maxFileBytes', 'includeGlobs', 'excludeGlobs']),
    );
    // Sent alongside a clear, a value would re-pin the field the reader just released.
    expect(body.excludeGlobs).toBeUndefined();
  });

  it('sends an override when a field is taken off the default', async () => {
    const { user, dialog } = await openEdit(owning());

    await user.click(within(dialog).getByRole('checkbox', { name: /Never these: use the corpus default/ }));
    const globs = within(dialog).getByPlaceholderText('**/vendor/**, *.min.js');
    await user.clear(globs);
    await user.type(globs, '**/*.epub');
    await user.click(within(dialog).getByRole('button', { name: /^Save filters$/ }));

    await waitFor(() => expect(updateSource).toHaveBeenCalled());
    const body = updateSource.mock.calls[0][2];
    expect(body.excludeGlobs).toEqual(['**/*.epub']);
    expect(body.clear).not.toContain('excludeGlobs');
  });

  it('can say none at all against a corpus that excludes something', async () => {
    // The case null-versus-empty exists for. A cleared box that is not inherited is an
    // empty array, which is an override rather than an absence.
    const { user, dialog } = await openEdit(owning());

    await user.click(within(dialog).getByRole('checkbox', { name: /Never these: use the corpus default/ }));
    await user.clear(within(dialog).getByPlaceholderText('**/vendor/**, *.min.js'));
    await user.click(within(dialog).getByRole('button', { name: /^Save filters$/ }));

    await waitFor(() => expect(updateSource).toHaveBeenCalled());
    expect(updateSource.mock.calls[0][2].excludeGlobs).toEqual([]);
  });

  it('refuses a cap of zero rather than indexing nothing', async () => {
    const { user, dialog } = await openEdit(owning());

    await user.click(within(dialog).getByRole('checkbox', { name: /Largest file: use the corpus default/ }));
    // Exact: the inherit checkbox's own label also contains "Largest file".
    const cap = within(dialog).getByLabelText('Largest file (MB)');
    await user.clear(cap);
    await user.type(cap, '0');

    expect(await within(dialog).findByText(/A cap of zero indexes nothing/)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: /^Save filters$/ })).toBeDisabled();
  });

  it('accepts a cap that is not a round number of megabytes', async () => {
    // Found in a browser, not here: `step` on a number input makes anything off the grid
    // invalid, and an invalid field makes the browser refuse to submit the form WITHOUT
    // saying anything. A source on the 256 KB default is 0.25 MB, which is not 0.01 + n/2,
    // so its own edit dialog could not be saved and nothing on screen said why.
    //
    // A size cap is a free value. There is no grid for it to be on.
    const { dialog } = await openEdit(owning({ ownMaxFileBytes: 262_144, maxFileBytes: 262_144 }));

    const cap = within(dialog).getByLabelText('Largest file (MB)') as HTMLInputElement;

    expect(cap.value).toBe('0.25');
    expect(cap.validity.stepMismatch).toBe(false);
    expect(cap.checkValidity()).toBe(true);
    expect(cap.form!.checkValidity()).toBe(true);
  });
});

/**
 * A history source's settings after it was added. The row had no editor, so changing the
 * ref meant the API or removing the source and re-reading every commit.
 */
describe('editing a history source', () => {
  const history = () =>
    corpus({
      sources: [
        source({
          id: 's2', kind: 'githistory', rootPath: 'api-repo', fileCount: 174,
          includeGlobs: [], ownIncludeGlobs: null,
          git: { ref: 'HEAD', includeMessage: true, includeStat: true, includeDiff: false, maxDiffBytes: 65536, includeMerges: false, maxCommits: null, since: null },
        }),
      ],
    });

  async function openEditor() {
    const user = userEvent.setup();
    getCorpus.mockResolvedValue(history());
    render(<CorpusDetail {...props} />);
    await user.click(await screen.findByRole('button', { name: 'Edit history settings for api-repo' }));
    return { user, dialog: await screen.findByRole('dialog') };
  }

  it('sends every setting, since the server replaces them all', async () => {
    updateSource.mockResolvedValue({});
    const { user, dialog } = await openEditor();

    const ref = within(dialog).getByLabelText('Ref');
    await user.clear(ref);
    await user.type(ref, 'origin/main');
    await user.click(within(dialog).getByRole('button', { name: /Save history settings/ }));

    await waitFor(() => expect(updateSource).toHaveBeenCalled());
    const [name, id, body] = updateSource.mock.calls[0];
    expect([name, id]).toEqual(['docs', 's2']);
    expect(body.git).toEqual({
      ref: 'origin/main', includeMessage: true, includeStat: true, includeDiff: false,
      maxDiffBytes: 65536, includeMerges: false, maxCommits: null, keepIndexed: false, since: null,
    });
    // Following the corpus, as the source did before: nothing new pinned against it.
    expect(body.clear).toEqual(['includeGlobs']);
    expect(body.includeGlobs).toBeUndefined();
  });

  it('shows a refusal in the dialog, which stays open', async () => {
    // What the server says of a ref git cannot be asked with. Reported anywhere else, it
    // lands behind the dialog, and closing it would say the save had worked.
    updateSource.mockRejectedValue(new ApiError(400, 'Unusable history settings',
      "'main..other' is not a usable ref. A branch, a tag or an object name."));
    const { user, dialog } = await openEditor();

    const ref = within(dialog).getByLabelText('Ref');
    await user.clear(ref);
    await user.type(ref, 'main..other');
    await user.click(within(dialog).getByRole('button', { name: /Save history settings/ }));

    expect(await within(dialog).findByText(/is not a usable ref/)).toBeInTheDocument();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(props.onError).not.toHaveBeenCalled();
  });

  /**
   * The two kinds of change cost different amounts. Which commits are selected adds and
   * removes documents; what a document holds changes every one of them.
   */
  it('says when a change re-reads every commit', async () => {
    const { user, dialog } = await openEditor();

    const ref = within(dialog).getByLabelText('Ref');
    await user.clear(ref);
    await user.type(ref, 'origin/main');
    expect(within(dialog).getByText(/documents already indexed\s+are kept/)).toBeInTheDocument();

    await user.click(within(dialog).getByRole('checkbox', { name: /Include the diff/ }));
    expect(within(dialog).getByText(/Saving re-reads every commit/)).toBeInTheDocument();
  });

  /**
   * The content fingerprint sorts the paths before hashing, so the same paths in another
   * order make the same documents and nothing is re-read. Warning otherwise would put
   * someone off a harmless save.
   */
  it('does not call reordering the paths a re-read, and does call a new path one', async () => {
    const user = userEvent.setup();
    getCorpus.mockResolvedValue(
      corpus({
        sources: [
          source({
            id: 's2', kind: 'githistory', rootPath: 'api-repo', fileCount: 174,
            includeGlobs: ['src/**', 'docs/**'], ownIncludeGlobs: ['src/**', 'docs/**'],
            git: { ref: 'HEAD', includeMessage: true, includeStat: true, includeDiff: false, maxDiffBytes: 65536, includeMerges: false, maxCommits: null, since: null },
          }),
        ],
      }),
    );
    render(<CorpusDetail {...props} />);
    await user.click(await screen.findByRole('button', { name: 'Edit history settings for api-repo' }));
    const dialog = await screen.findByRole('dialog');

    const paths = within(dialog).getByLabelText('Only these paths');
    await user.clear(paths);
    await user.type(paths, 'docs/**, src/**');
    expect(within(dialog).getByText(/documents already indexed\s+are kept/)).toBeInTheDocument();

    await user.type(paths, ', tests/**');
    expect(within(dialog).getByText(/Saving re-reads every commit/)).toBeInTheDocument();
  });

  /**
   * Keeping is a property of a limit: without one every commit is indexed already, and the
   * server refuses the setting there. Offered only beside a limit, and cleared with it.
   */
  it('offers keeping indexed commits only beside a commit limit', async () => {
    updateSource.mockResolvedValue({});
    const { user, dialog } = await openEditor();

    expect(within(dialog).queryByRole('checkbox', { name: /Keep commits once indexed/ })).not.toBeInTheDocument();

    const limit = within(dialog).getByLabelText(/Newest commits only/);
    await user.type(limit, '500');
    await user.click(within(dialog).getByRole('checkbox', { name: /Keep commits once indexed/ }));
    await user.click(within(dialog).getByRole('button', { name: /Save history settings/ }));

    await waitFor(() => expect(updateSource).toHaveBeenCalled());
    expect(updateSource.mock.calls[0][2].git).toMatchObject({ maxCommits: 500, keepIndexed: true });
  });

  it('clears keeping when the limit is cleared', async () => {
    updateSource.mockResolvedValue({});
    const { user, dialog } = await openEditor();

    const limit = within(dialog).getByLabelText(/Newest commits only/);
    await user.type(limit, '500');
    await user.click(within(dialog).getByRole('checkbox', { name: /Keep commits once indexed/ }));
    await user.clear(limit);

    expect(within(dialog).queryByRole('checkbox', { name: /Keep commits once indexed/ })).not.toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: /Save history settings/ }));

    await waitFor(() => expect(updateSource).toHaveBeenCalled());
    expect(updateSource.mock.calls[0][2].git).toMatchObject({ maxCommits: null, keepIndexed: false });
  });

  it('warns that turning keeping off removes what is past the limit', async () => {
    const user = userEvent.setup();
    getCorpus.mockResolvedValue(
      corpus({
        sources: [
          source({
            id: 's2', kind: 'githistory', rootPath: 'api-repo', fileCount: 900,
            includeGlobs: [], ownIncludeGlobs: null,
            git: { ref: 'HEAD', includeMessage: true, includeStat: true, includeDiff: false, maxDiffBytes: 65536, includeMerges: false, maxCommits: 500, keepIndexed: true, since: null },
          }),
        ],
      }),
    );
    render(<CorpusDetail {...props} />);
    await user.click(await screen.findByRole('button', { name: 'Edit history settings for api-repo' }));
    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).queryByText(/leave the index on the refresh/)).not.toBeInTheDocument();
    await user.click(within(dialog).getByRole('checkbox', { name: /Keep commits once indexed/ }));
    expect(within(dialog).getByText(/commits past the newest\s+500\s+leave the index on the refresh/)).toBeInTheDocument();
  });

  it('is not offered the file settings', async () => {
    const { dialog } = await openEditor();

    expect(within(dialog).queryByLabelText(/Largest file/)).not.toBeInTheDocument();
    expect(within(dialog).queryByText(/Honour \.gitignore/)).not.toBeInTheDocument();
    expect(within(dialog).queryByText(/Never these/)).not.toBeInTheDocument();
  });
});

describe('the corpus default filters', () => {
  async function openDefaults(c: Corpus) {
    const user = userEvent.setup();
    getCorpus.mockResolvedValue(c);
    render(<CorpusDetail {...props} />);
    await user.click(await screen.findByRole('button', { name: /Default filters/ }));
    return { user, dialog: await screen.findByRole('dialog') };
  }

  it('sends globs the sources will inherit', async () => {
    const { user, dialog } = await openDefaults(corpus());

    await user.type(within(dialog).getByPlaceholderText(/Designer\.cs/), '**/*.test.ts');
    await user.click(within(dialog).getByRole('button', { name: /^Save defaults$/ }));

    await waitFor(() => expect(updateCorpus).toHaveBeenCalled());
    expect(updateCorpus.mock.calls[0][1].defaults).toMatchObject({ excludeGlobs: ['**/*.test.ts'] });
  });

  it('sends null for a default left unset rather than a value nobody chose', async () => {
    // Null means the server's setting applies. Sending 0 or false here would quietly
    // impose a cap or a gitignore rule on every source in the corpus.
    const { user, dialog } = await openDefaults(corpus());

    await user.click(within(dialog).getByRole('button', { name: /^Save defaults$/ }));

    await waitFor(() => expect(updateCorpus).toHaveBeenCalled());
    expect(updateCorpus.mock.calls[0][1].defaults).toEqual({
      useGitignore: null,
      maxFileBytes: null,
      includeGlobs: null,
      excludeGlobs: null,
    });
  });

  it('warns that a source setting its own filters keeps them', async () => {
    // Otherwise saving a default that changes nothing visible reads as a bug.
    const { dialog } = await openDefaults(corpus({
      sources: [source({ ownUseGitignore: true, ownMaxFileBytes: 1024, ownIncludeGlobs: [], ownExcludeGlobs: [] })],
    }));

    expect(within(dialog).getByText(/1 source sets some of its own filters/)).toBeInTheDocument();
  });
});


/**
 * A corpus of 190 files showed 100 of them and said nothing, and a 27,000-file one
 * showed 300.
 *
 * Three limits disagreed and the one guard against it could never fire: the API paged at
 * 100 when asked for no limit, the list rendered at most 300 of what it held, and the
 * notice wanted more than 300 FETCHED rows. The visible symptom was a file that had just
 * been added being absent from the list while indexed, searchable and returned by the
 * API.
 *
 * A better notice was the first fix and the wrong one. Filtering and ordering a page can
 * only ever see that page, so a name that IS in the corpus still came back as no match.
 * The server decides both, and the page can be left.
 */
describe('the file list', () => {
  const file = (relativePath: string) => ({
    id: relativePath,
    relativePath,
    status: 'indexed',
    statusDetail: null,
    chunkCount: 3,
    sizeBytes: 1024,
    language: null,
    mediaType: null,
    extractedChars: 100,
    indexedUtc: new Date().toISOString(),
  });

  const page = (count: number, total: number, from = 0) => ({
    total,
    chunkSet: 'default',
    files: Array.from({ length: count }, (_, i) => file(`book-${from + i}.pdf`)),
  });

  /** The options object the component passed on its most recent fetch. */
  const lastQuery = () => listFiles.mock.calls.at(-1)?.[1] as Record<string, unknown>;

  it('asks for one page rather than everything', async () => {
    listFiles.mockResolvedValue(page(100, 27033));

    render(<CorpusDetail {...props} />);
    await waitFor(() => expect(listFiles).toHaveBeenCalled());

    expect(lastQuery().limit).toBe(100);
    expect(lastQuery().offset).toBe(0);
  });

  it('says which page of how many matches it is showing', async () => {
    listFiles.mockResolvedValue(page(100, 27033));

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText(/1–100 of 27,033/)).toBeInTheDocument();
  });

  /**
   * The point of the change. A client-side filter searches the rows it fetched, so on a
   * corpus larger than a page a file that exists reads as one that does not.
   */
  it('sends the name filter to the server instead of filtering the page', async () => {
    listFiles.mockResolvedValue(page(100, 27033));
    render(<CorpusDetail {...props} />);
    await screen.findByRole('button', { name: 'book-0.pdf' });

    await userEvent.type(screen.getByLabelText('Filter files by name'), 'papers');

    await waitFor(() => expect(lastQuery().name).toBe('papers'));
  });

  it('returns to the first page when the filter changes', async () => {
    listFiles.mockResolvedValue(page(100, 27033));
    render(<CorpusDetail {...props} />);
    await screen.findByRole('button', { name: 'book-0.pdf' });

    await userEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(lastQuery().offset).toBe(100));

    await userEvent.type(screen.getByLabelText('Filter files by name'), 'papers');

    // Paging arithmetic over a different result set lands somewhere arbitrary, and an
    // empty page reads as no matches.
    await waitFor(() => expect(lastQuery().offset).toBe(0));
  });

  it('pages forward and back', async () => {
    listFiles.mockResolvedValue(page(100, 250));
    render(<CorpusDetail {...props} />);
    await screen.findByRole('button', { name: 'book-0.pdf' });

    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();

    await userEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(lastQuery().offset).toBe(100));

    await userEvent.click(screen.getByRole('button', { name: 'Previous' }));
    await waitFor(() => expect(lastQuery().offset).toBe(0));
  });

  /**
   * The debounce used to be armed by any keystroke, and by the first render, and it reset
   * the offset when it fired whether or not the query had changed. A page turned inside
   * that window went back to the first one, with nothing on screen saying why.
   *
   * It is also what made `pages forward and back` fail on a slow runner: the click landed
   * before the timer armed at mount, and the reset arrived between the click and the
   * assertion.
   */
  it('keeps the page when the filter is touched without changing', async () => {
    listFiles.mockResolvedValue(page(100, 250));
    render(<CorpusDetail {...props} />);
    await screen.findByRole('button', { name: 'book-0.pdf' });

    await userEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(lastQuery().offset).toBe(100));

    // The filter is trimmed, so a trailing space matches exactly the same rows.
    await userEvent.type(screen.getByLabelText('Filter files by name'), ' ');
    await new Promise((resolve) => setTimeout(resolve, 400));   // past the 250ms pause

    expect(lastQuery().offset).toBe(100);
  });

  it('stops paging at the end', async () => {
    listFiles.mockResolvedValue(page(40, 40));

    render(<CorpusDetail {...props} />);
    await screen.findByRole('button', { name: 'book-0.pdf' });

    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
  });

  it('sorts on the server', async () => {
    listFiles.mockResolvedValue(page(100, 27033));
    render(<CorpusDetail {...props} />);
    await screen.findByRole('button', { name: 'book-0.pdf' });

    await userEvent.click(screen.getByRole('radio', { name: 'size' }));

    await waitFor(() => expect(lastQuery().sort).toBe('size'));
  });

  /**
   * "No match" now means the whole corpus was searched, which is the difference between
   * a file being absent and being on another page.
   */
  it('says the whole corpus was searched when nothing matches', async () => {
    listFiles.mockResolvedValue({ total: 0, chunkSet: 'default', files: [] });

    render(<CorpusDetail {...props} />);
    await userEvent.type(await screen.findByLabelText('Filter files by name'), 'zzz');

    expect(await screen.findByText('No file matches that')).toBeInTheDocument();
    expect(screen.getByText(/Searched all 12 files in this corpus/)).toBeInTheDocument();
  });
});

describe('when a run finishes', () => {
  /** The last event of a run, as the server sends it: the state name in `phase`. */
  const finished = {
    c1: {
      jobId: 'j1', corpusId: 'c1', phase: 'Succeeded', filesTotal: 12,
      filesDone: 12, filesSkipped: 0, filesFailed: 0, chunksWritten: 114,
      currentFile: null, error: null,
    },
  } as unknown as typeof props.live;

  it('re-reads the corpus, because the counts on screen are from before the run', async () => {
    render(<CorpusDetail {...props} live={finished} />);

    // Once for the mount, once because the run ended.
    await waitFor(() => expect(getCorpus).toHaveBeenCalledTimes(2));
  });

  it('does not re-read it again on every filter and sort after that', async () => {
    // Nothing clears a finished event out of `live`, and `load` changes identity with
    // the filter, the sort and the page — so a condition rather than a key fetched the
    // corpus twice for every interaction for the rest of the session.
    render(<CorpusDetail {...props} live={finished} />);
    await waitFor(() => expect(getCorpus).toHaveBeenCalledTimes(2));

    await userEvent.click(await screen.findByRole('radio', { name: 'size' }));

    // The sort change reloads once. The finished event must not add a second.
    await waitFor(() => expect(getCorpus).toHaveBeenCalledTimes(3));
    await new Promise((r) => setTimeout(r, 50));
    expect(getCorpus).toHaveBeenCalledTimes(3);
  });
});

describe('the chunk sets on a corpus page', () => {
  /**
   * They sat under the file list: 5.8 screens down on a 1,834-file corpus, so which model a
   * corpus used was effectively not on the page.
   */
  // Node 25 defines its own global localStorage, and without a storage file it is a stub
  // with no methods, which shadows jsdom's. The page guards every call, so it renders either
  // way; whether it remembers can only be observed through a working Storage.
  beforeEach(() => {
    const items = new Map<string, string>();
    vi.stubGlobal('localStorage', {
      get length() { return items.size; },
      clear: () => items.clear(),
      getItem: (k: string) => items.get(k) ?? null,
      key: (i: number) => [...items.keys()][i] ?? null,
      removeItem: (k: string) => { items.delete(k); },
      setItem: (k: string, v: string) => { items.set(k, String(v)); },
    } satisfies Storage);
    getCorpus.mockResolvedValue(corpus({ chunkSets: [chunkSet()] }));
  });
  afterEach(() => vi.unstubAllGlobals());

  it('sit above the file list, collapsed, naming the set search uses', async () => {
    render(<CorpusDetail {...props} />);

    const toggle = await screen.findByRole('button', { name: /Chunk sets/ });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(toggle).toHaveTextContent('docs:default');

    const files = screen.getByRole('radiogroup', { name: 'File status' });
    expect(toggle.compareDocumentPosition(files) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('stay open once opened, in this browser', async () => {
    const first = render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Chunk sets/ }));
    expect(localStorage.getItem('dexicon.chunkSets.open')).toBe('1');
    first.unmount();

    render(<CorpusDetail {...props} />);

    expect(await screen.findByRole('button', { name: /Chunk sets/ })).toHaveAttribute('aria-expanded', 'true');
  });

  it('are opened from the full reindex dialog, which sends a model change there', async () => {
    // jsdom has no layout, so no scrollIntoView; stubbed to see that it is asked for.
    const scrollIntoView = vi.fn();
    Element.prototype.scrollIntoView = scrollIntoView;
    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    await userEvent.click(await screen.findByRole('button', { name: 'Add one under Chunk sets' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    const toggle = screen.getByRole('button', { name: /Chunk sets/ });
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    // Focus goes where the page went, not back to Full reindex.
    await waitFor(() => expect(toggle).toHaveFocus());
    await waitFor(() => expect(scrollIntoView).toHaveBeenCalled());
    delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView;
  });
});

describe('the full reindex', () => {
  /**
   * It sat one click from Refresh with nothing between them, and the two are not
   * comparable: Refresh embeds what moved, this embeds everything.
   */
  it('asks before it queues anything', async () => {
    render(<CorpusDetail {...props} />);

    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    expect(reindex).not.toHaveBeenCalled();
    expect(await screen.findByText(/Full reindex of docs/)).toBeInTheDocument();
  });

  it('queues the full one once confirmed', async () => {
    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    await userEvent.click(await screen.findByRole('button', { name: /Reindex everything/ }));

    expect(reindex).toHaveBeenCalledWith('docs', true);
  });

  it('offers the cheap one at the moment of doubt', async () => {
    // The dialog is where someone finds out this is not what they wanted. Making them
    // cancel and go looking for the other button is where they confirm instead.
    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    await userEvent.click(await screen.findByRole('button', { name: /Refresh instead/ }));

    expect(reindex).toHaveBeenCalledWith('docs', false);
  });

  it('queues nothing on cancel', async () => {
    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    await userEvent.click(await screen.findByRole('button', { name: 'Cancel' }));

    expect(reindex).not.toHaveBeenCalled();
    expect(screen.queryByText(/Full reindex of docs/)).not.toBeInTheDocument();
  });

  it('says why in the dialog when the server refuses', async () => {
    reindex.mockRejectedValue(new ApiError(503, 'Embedding unavailable', 'Ollama did not answer.'));
    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    await userEvent.click(await screen.findByRole('button', { name: /Reindex everything/ }));

    const dialog = screen.getByRole('dialog');
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(/did not answer/);
    expect(props.onError).not.toHaveBeenCalled();
  });

  it('clears the last failure when it tries again', async () => {
    // A stale message beside a retry in flight reads as that retry's result.
    reindex
      .mockRejectedValueOnce(new ApiError(503, 'Embedding unavailable', 'Ollama did not answer.'))
      .mockReturnValueOnce(new Promise(() => {}));
    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));
    const dialog = screen.getByRole('dialog');

    await userEvent.click(within(dialog).getByRole('button', { name: /Reindex everything/ }));
    await within(dialog).findByRole('alert');

    await userEvent.click(within(dialog).getByRole('button', { name: /Reindex everything/ }));
    await waitFor(() => expect(within(dialog).queryByRole('alert')).not.toBeInTheDocument());
  });

  it('opens with focus on Cancel', async () => {
    // The link to Chunk sets is the first control in the dialog, so focus started there.
    // Cancel is the one control that does nothing.
    render(<CorpusDetail {...props} />);

    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus());
  });

  it('names every set and what it will embed with', async () => {
    // The job names no chunk set, so the indexer runs each of them. A corpus cut two ways
    // costs both, and nothing else on the screen says so.
    getCorpus.mockResolvedValue(
      corpus({
        chunkSets: [
          chunkSet({ id: 'a', name: 'default', chunkCount: 131 }),
          chunkSet({ id: 'b', name: 'gemma', isDefault: false, embeddingModel: 'embeddinggemma', chunkCount: 135 }),
        ],
      }),
    );

    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('docs:default')).toBeInTheDocument();
    expect(within(dialog).getByText('docs:gemma')).toBeInTheDocument();
    expect(within(dialog).getByText('embeddinggemma')).toBeInTheDocument();
    // 131 + 135 across both sets, not the default's alone. Chunks sum honestly: each one
    // belongs to exactly one set.
    expect(within(dialog).getByText('266')).toBeInTheDocument();
    expect(within(dialog).getByText(/all 2 of this corpus’s chunk sets/)).toBeInTheDocument();
  });

  it('counts files per set rather than totalling them', async () => {
    // `corpus.fileCount` is the DEFAULT set's, by design — it is what an unqualified
    // search reaches. Labelling a run over every set with it says a number that is not
    // the work; summing across sets says one that is not files, since a file held in two
    // sets is one file.
    getCorpus.mockResolvedValue(
      corpus({
        fileCount: 16,
        chunkSets: [
          chunkSet({ id: 'a', name: 'default', fileCount: 16, chunkCount: 131 }),
          chunkSet({ id: 'b', name: 'gemma', isDefault: false, fileCount: 14, chunkCount: 135 }),
        ],
      }),
    );

    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    const dialog = await screen.findByRole('dialog');
    const text = dialog.textContent?.replace(/\s+/g, ' ') ?? '';

    expect(text).toContain('16 files');
    expect(text).toContain('14 files');
    // Neither the default's count nor a sum stands in for the whole run.
    expect(text).not.toContain('30 files');
  });

  it('calls multi-set failures failures, because one file can be two of them', async () => {
    getCorpus.mockResolvedValue(
      corpus({
        chunkSets: [
          chunkSet({ id: 'a', name: 'default', failedCount: 8 }),
          chunkSet({ id: 'b', name: 'gemma', isDefault: false, failedCount: 8 }),
        ],
      }),
    );

    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    const text = (await screen.findByRole('dialog')).textContent?.replace(/\s+/g, ' ') ?? '';

    expect(text).toContain('16 failures across 2 chunk sets');
    expect(text).not.toContain('16 files failed');
  });

  it('says a model change needs a chunk set rather than this', async () => {
    // The reason people reach for this button, and the one thing it cannot do: a model is
    // a different vector space.
    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    expect(await screen.findByText(/To move to another embedding model, this is not the button/))
      .toBeInTheDocument();
  });

  it('mentions the failures it will retry, and that a refresh retries them too', async () => {
    // Otherwise a failed file is a reason to reach for the expensive button when the cheap
    // one would have done it: a failed file has no fingerprint to skip on.
    getCorpus.mockResolvedValue(corpus({ chunkSets: [chunkSet({ failedCount: 8 })] }));

    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    expect(await screen.findByText(/8 files failed last time/)).toBeInTheDocument();
  });

  it('fails commits on a history corpus, not files', async () => {
    // `unitFor` is used for every other count on this screen. Hardcoding "files" here
    // made the warning contradict the source row above it.
    getCorpus.mockResolvedValue(
      corpus({
        sources: [source({ id: 's2', kind: 'githistory' })],
        chunkSets: [chunkSet({ failedCount: 3 })],
      }),
    );

    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    const text = (await screen.findByRole('dialog')).textContent?.replace(/\s+/g, ' ') ?? '';

    expect(text).toContain('3 commits failed last time');
    expect(text).not.toContain('3 files failed');
  });

  it('does not claim the item being reindexed stays searchable', async () => {
    // The indexer deletes a file's vectors and then writes the replacement, so the one in
    // hand IS missing for that moment — and stays missing if its embedding fails or the
    // job is interrupted. "Search keeps working" was true of the corpus and false of the
    // item, which is the half someone would notice.
    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    const text = (await screen.findByRole('dialog')).textContent?.replace(/\s+/g, ' ') ?? '';

    expect(text).toContain('not cleared up front');
    expect(text).toContain('deleted before the new ones are written');
    expect(text).toContain('stays missing until a later pass');
    expect(text).not.toContain('Search keeps working while it runs');
  });

  it('does not promise to embed what it will only record', async () => {
    // A full pass reads everything and embeds what it can chunk. An excluded, oversize or
    // empty item gets a row and no vectors, and a failure gets neither — so "every file is
    // embedded again" was a claim about work that does not happen.
    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    const text = (await screen.findByRole('dialog')).textContent?.replace(/\s+/g, ' ') ?? '';

    expect(text).toContain('re-embedded if it can be read and chunked');
    expect(text).toContain('excluded, oversize or empty is recorded without being embedded');
    expect(text).not.toContain('Every file is read, chunked and embedded again');
  });

  it('describes the pass in the corpus’s own unit', async () => {
    getCorpus.mockResolvedValue(
      corpus({ sources: [source({ id: 's2', kind: 'githistory' })], chunkSets: [chunkSet()] }),
    );

    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    const text = (await screen.findByRole('dialog')).textContent?.replace(/\s+/g, ' ') ?? '';

    expect(text).toContain('Every commit is read again');
    expect(text).not.toContain('Every file is read again');
  });

  it('says the chunk figure is what is held now, not what the run will produce', async () => {
    // There is no planned count to ask for: a pending row has no chunks yet, a failed one
    // keeps its last, and a changed file will produce a different number. Announcing this
    // as the work would let a corpus mid-sweep claim it has nothing to do.
    getCorpus.mockResolvedValue(corpus({ chunkSets: [chunkSet({ chunkCount: 131 })] }));

    render(<CorpusDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: /Full reindex/ }));

    const text = (await screen.findByRole('dialog')).textContent?.replace(/\s+/g, ' ') ?? '';

    expect(text).toContain('holds 131 chunks today');
    expect(text).not.toContain('chunks to embed');
  });
});
