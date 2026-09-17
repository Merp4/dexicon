import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CorpusDetail } from './App';
import { ApiError } from './api';
import type { Corpus, IndexedFileText } from './api';

/**
 * Opening a file.
 *
 * You could search a file, list it, and read a forty-line snippet of it, and there was no
 * way to open it — the REST API had no endpoint for it at all, though MCP has had one
 * since the resources landed. A file you can see listed and cannot open is the screen
 * telling you it knows something it will not say.
 *
 * What it shows is what was INDEXED, not the file on disk: that is what search is actually
 * searching, it is the thing worth looking at when a result is surprising, and for an
 * uploaded PDF there is no file to read instead.
 */
const getCorpus = vi.fn();
const listFiles = vi.fn();
const fileText = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: {
    getCorpus: (...a: unknown[]) => getCorpus(...a),
    listFiles: (...a: unknown[]) => listFiles(...a),
    fileText: (...a: unknown[]) => fileText(...a),
    browse: vi.fn().mockResolvedValue({ entries: [] }),
    reindex: vi.fn(),
  },
}));

const corpus = {
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
  fileCount: 1,
  chunkCount: 4,
  skippedCount: 0,
  failedCount: 0,
  sources: [],
  chunkSets: [],
} as unknown as Corpus;

const files = {
  files: [
    {
      id: 'f1',
      relativePath: '05-search.md',
      status: 'indexed',
      statusDetail: null,
      language: 'markdown',
      sizeBytes: 7700,
      chunkCount: 4,
      indexedUtc: new Date().toISOString(),
    },
  ],
};

function text(over: Partial<IndexedFileText> = {}): IndexedFileText {
  return {
    corpus: 'docs',
    chunkSet: 'default',
    path: '05-search.md',
    startLine: 1,
    endLine: 4,
    gaps: 0,
    truncated: false,
    text: 'first line\nsecond line\nthird line\nfourth line',
    offset: 0,
    totalChars: 44,
    nextOffset: null,
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
  getCorpus.mockResolvedValue(corpus);
  listFiles.mockResolvedValue(files);
  fileText.mockResolvedValue(text());
});

async function openTheFile() {
  const user = userEvent.setup();
  render(<CorpusDetail {...props} />);

  await user.click(await screen.findByRole('button', { name: '05-search.md' }));
  return { user, dialog: await screen.findByRole('dialog') };
}

describe('opening a file from the list', () => {
  it('asks for the file the row names', async () => {
    await openTheFile();

    await waitFor(() => expect(fileText).toHaveBeenCalledWith('docs', '05-search.md'));
  });

  it('numbers the lines from the file, not from the top of the passage', async () => {
    // The viewer can be showing a window into a file. Numbering from 1 would be
    // confidently wrong, which is worse than not numbering at all.
    fileText.mockResolvedValue(text({ startLine: 40, endLine: 43 }));

    const { dialog } = await openTheFile();

    expect(await within(dialog).findByText('40')).toBeInTheDocument();
    expect(within(dialog).getByText('43')).toBeInTheDocument();
    expect(within(dialog).queryByText('1')).not.toBeInTheDocument();
  });

  it('says which chunk set it is reading', async () => {
    // Two sets over the same file can hold different chunkings of it, so "the file" is
    // not a single thing.
    const { dialog } = await openTheFile();

    expect(await within(dialog).findByText('docs:default')).toBeInTheDocument();
  });

  it('announces a gap rather than closing it silently', async () => {
    // Chunks from one pass tile the file, so a gap means the index really is missing
    // those lines. Butting the two ends together would hand someone a file that reads as
    // contiguous and is not.
    fileText.mockResolvedValue(text({ gaps: 2 }));

    const { dialog } = await openTheFile();

    expect(await within(dialog).findByText('2 gaps in the index')).toBeInTheDocument();
  });

  it('says how much of the file is on screen, not merely that it stopped', async () => {
    // "truncated" states a problem and offers no way out of it. A book runs to two or
    // three million characters, so the old badge meant the first chapter or two and
    // nothing else, with no indication of how much was missing.
    fileText.mockResolvedValue(text({ truncated: true, totalChars: 400, nextOffset: 44 }));

    const { dialog } = await openTheFile();

    expect(await within(dialog).findByText(/11% of 400 characters/)).toBeInTheDocument();
  });

  it('reads on from where the window stopped', async () => {
    const user = userEvent.setup();
    fileText.mockResolvedValueOnce(text({ truncated: true, totalChars: 88, nextOffset: 44 }));
    const { dialog } = await openTheFile();

    fileText.mockResolvedValueOnce(text({
      text: '\nfifth line', offset: 44, totalChars: 88, nextOffset: null, truncated: false,
    }));
    await user.click(await within(dialog).findByRole('button', { name: /Read on/ }));

    // Appended, not replaced: scrolling back to what you already read is the point.
    expect(await within(dialog).findByText(/fifth line/)).toBeInTheDocument();
    expect(within(dialog).getByText(/first line/)).toBeInTheDocument();
    expect(fileText).toHaveBeenLastCalledWith('docs', '05-search.md', 44);
  });

  it('offers nothing to read on to at the end of a file', async () => {
    fileText.mockResolvedValue(text());

    const { dialog } = await openTheFile();

    await within(dialog).findByText(/first line/);
    expect(within(dialog).queryByRole('button', { name: /Read on/ })).not.toBeInTheDocument();
  });

  it('reports a file that is not indexed instead of showing an empty box', async () => {
    // A path can be right and still not be in THIS chunk set.
    const onError = vi.fn();
    fileText.mockRejectedValue(new ApiError(404, 'Not indexed', "No indexed file '05-search.md'."));

    const user = userEvent.setup();
    render(<CorpusDetail {...props} onError={onError} />);
    await user.click(await screen.findByRole('button', { name: '05-search.md' }));

    await waitFor(() => expect(onError).toHaveBeenCalled());
    expect(String(onError.mock.calls[0][0])).toContain('No indexed file');
  });

  it('closes again', async () => {
    const { user, dialog } = await openTheFile();

    await user.click(within(dialog).getByRole('button', { name: /close/i }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });
});
