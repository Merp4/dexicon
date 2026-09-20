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
    reindex: vi.fn(),
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

  it('warns before indexing the same folder twice', async () => {
    const { user, dialog } = await openAddSource();

    await user.click(await within(dialog).findByRole('button', { name: /api-repo/ }));

    expect(within(dialog).getByText(/already indexes that folder/i)).toBeInTheDocument();
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

    // 96 is what has been counted so far, not what the folder holds.
    expect(await screen.findByText('96 so far')).toBeInTheDocument();
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
    file('Designing Data-Intensive Applications, 2nd Edition.epub', 'f1'),
    file('Designing Data-Intensive Applications, 2nd Edition.pdf', 'f2'),
    file('Fundamentals of Software Architecture.pdf', 'f3'),
    file('src/Dexicon.Core/Auth/ScopeResolver.cs', 'f4'),
  ];

  beforeEach(() => listFiles.mockResolvedValue({ files: shelf }));

  it('narrows a shelf of books to the one being looked for', async () => {
    // Ninety-six titles, alphabetical, each present twice. The status tabs do not help
    // when every one of them is indexed and you want a particular book.
    const user = userEvent.setup();
    render(<CorpusDetail {...props} />);

    await user.type(await screen.findByLabelText('Filter files by name'), 'data-intensive');

    expect(screen.getAllByRole('button', { name: /Designing Data-Intensive/ })).toHaveLength(2);
    expect(screen.queryByRole('button', { name: /Fundamentals/ })).not.toBeInTheDocument();
  });

  it('says how many it is hiding rather than looking like an empty corpus', async () => {
    const user = userEvent.setup();
    render(<CorpusDetail {...props} />);

    await user.type(await screen.findByLabelText('Filter files by name'), 'zzzz');

    expect(screen.getByText('No file matches that')).toBeInTheDocument();
    expect(screen.getByText(/4 files in this view/)).toBeInTheDocument();
  });

  it('sets a document title in the body face and a path in monospace', async () => {
    // "…in the Field or in the Making, 3rd Edition.epub" wrapped as "3rd Editio / n.epub":
    // break-all is right for a path and wrong for a sentence.
    render(<CorpusDetail {...props} />);

    const book = await screen.findByRole('button', { name: /Fundamentals of Software/ });
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
    files: ['Internet of Things from Scratch.pdf'],
    ...over,
  });

  it('names the directory, the file, and what it means for search', async () => {
    coverage.mockResolvedValue({ gaps: [gap()] });

    render(<CorpusDetail {...props} />);

    expect(await screen.findByText(/covered by no source/)).toBeInTheDocument();
    expect(screen.getByText('books/manuals')).toBeInTheDocument();
    expect(screen.getByText('Internet of Things from Scratch.pdf')).toBeInTheDocument();
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
