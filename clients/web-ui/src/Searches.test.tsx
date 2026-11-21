import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SearchesView, queryFor, score, whatWasAsked } from './Searches';
import type { SearchLogItem, SearchLogPage } from './api';

/**
 * The searches screen: what a row says, what the notice says about what is kept, and that every filter
 * goes to the server. A filter applied to the rows already fetched cannot find the empty searches that
 * fell off the end of the page, which is what the log is for.
 */
const listSearches = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: { listSearches: (...a: unknown[]) => listSearches(...a) },
}));

let seq = 0;

function item(over: Partial<SearchLogItem> = {}): SearchLogItem {
  seq += 1;
  return {
    id: seq,
    atUtc: '2026-11-20T09:00:00Z',
    keyName: 'agent',
    surface: 'Mcp',
    corpora: 'docs',
    mode: 'hybrid',
    query: 'how are keys stored',
    queryChars: 19,
    hits: 3,
    topScore: 0.8125,
    tookMs: 42,
    degraded: false,
    noted: false,
    ...over,
  };
}

function page(items: SearchLogItem[], over: Partial<SearchLogPage> = {}): SearchLogPage {
  return {
    items,
    nextBefore: null,
    matching: items.length,
    oldestUtc: '2026-11-19T09:00:00Z',
    settings: { enabled: true, storesQueryText: true, retentionDays: 30, dropped: 0 },
    ...over,
  };
}

beforeEach(() => {
  listSearches.mockReset();
});

describe('what a row says', () => {
  it('shows the text that was kept', () => {
    expect(whatWasAsked({ query: 'alpha', queryChars: 5 })).toBe('alpha');
  });

  it('marks text that was cut', () => {
    expect(whatWasAsked({ query: 'alpha', queryChars: 600 })).toBe('alpha…');
  });

  it('says how long the query was when no text is kept', () => {
    expect(whatWasAsked({ query: null, queryChars: 1 })).toBe('Text not stored (1 character)');
    expect(whatWasAsked({ query: null, queryChars: 16 })).toBe('Text not stored (16 characters)');
  });

  it('shows a score to three places and nothing when there was no hit', () => {
    expect(score({ topScore: 0.8125 })).toBe('0.813');
    expect(score({ topScore: null })).toBe('');
  });
});

describe('the searches screen', () => {
  it('lists each search with its interface, its hits and its time taken', async () => {
    listSearches.mockResolvedValue(page([
      item({ keyName: 'hook', surface: 'Context', corpora: 'docs,books', query: 'what is a chunk set', hits: 0, topScore: null }),
      item({ keyName: 'agent', degraded: true, tookMs: 910 }),
    ]));

    render(<SearchesView onError={vi.fn()} />);

    const rows = within(await screen.findByRole('table')).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(2);
    expect(within(rows[0]).getByText('hook')).toBeInTheDocument();
    expect(within(rows[0]).getByText('Context')).toBeInTheDocument();
    expect(within(rows[0]).getByText('none')).toBeInTheDocument();
    expect(within(rows[1]).getByText('MCP tool')).toBeInTheDocument();
    expect(within(rows[1]).getByText('910 ms')).toBeInTheDocument();
    expect(within(rows[1]).getByText('keyword only')).toBeInTheDocument();
  });

  it('flags a search that did not name its interface', async () => {
    listSearches.mockResolvedValue(page([item({ surface: 'Other' })]));

    render(<SearchesView onError={vi.fn()} />);

    expect(await screen.findByText('Unknown')).toBeInTheDocument();
  });

  it('says how long the text is kept and that backups hold it', async () => {
    listSearches.mockResolvedValue(page([item()]));

    render(<SearchesView onError={vi.fn()} />);

    expect(await screen.findByText(/Kept for 30 days/)).toBeInTheDocument();
    expect(screen.getByText(/is included in backups of the catalogue/)).toBeInTheDocument();
  });

  it('says when no text is stored', async () => {
    listSearches.mockResolvedValue(page([item({ query: null })], {
      settings: { enabled: true, storesQueryText: false, retentionDays: 7, dropped: 0 },
    }));

    render(<SearchesView onError={vi.fn()} />);

    expect(await screen.findByText(/Query text is not stored, only its length/)).toBeInTheDocument();
    expect(screen.getByText(/Kept for 7 days/)).toBeInTheDocument();
  });

  it('says so when the log is switched off', async () => {
    listSearches.mockResolvedValue(page([], {
      settings: { enabled: false, storesQueryText: true, retentionDays: 30, dropped: 0 },
    }));

    render(<SearchesView onError={vi.fn()} />);

    const notice = (await screen.findByText(/DEXICON__SEARCHLOG__ENABLED/)).closest('[role="status"]') as HTMLElement;
    expect(notice).toHaveTextContent('The search log is switched off (DEXICON__SEARCHLOG__ENABLED), so new searches are not recorded.');
    expect(screen.getByText('No searches recorded')).toBeInTheDocument();
    expect(screen.getByText('The search log is switched off.')).toBeInTheDocument();
  });

  it('reports searches the log could not keep', async () => {
    listSearches.mockResolvedValue(page([item()], {
      settings: { enabled: true, storesQueryText: true, retentionDays: 30, dropped: 3 },
    }));

    render(<SearchesView onError={vi.fn()} />);

    expect(await screen.findByText(/3 searches could not be recorded since the server started/)).toBeInTheDocument();
  });

  it('asks the server for the searches that found nothing, not the rows already here', async () => {
    listSearches.mockResolvedValue(page([item()]));
    const user = userEvent.setup();
    render(<SearchesView onError={vi.fn()} />);
    await screen.findByRole('table');

    await user.click(screen.getByRole('radio', { name: 'Found nothing' }));

    await vi.waitFor(() => expect(listSearches).toHaveBeenLastCalledWith(expect.objectContaining({ noResults: true })));
  });

  it('sends a typed key and corpus filter when the form is submitted, not on each keystroke', async () => {
    listSearches.mockResolvedValue(page([item()]));
    const user = userEvent.setup();
    render(<SearchesView onError={vi.fn()} />);
    await screen.findByRole('table');
    listSearches.mockClear();

    await user.type(screen.getByLabelText('Key name'), 'hook');
    await user.type(screen.getByLabelText('Corpus name'), 'doc');
    expect(listSearches).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Filter' }));

    await vi.waitFor(() => expect(listSearches).toHaveBeenLastCalledWith(expect.objectContaining({ key: 'hook', corpus: 'doc' })));
  });

  it('continues from the id the last page ended on', async () => {
    listSearches.mockResolvedValueOnce(page([item({ id: 900 })], { nextBefore: 900, matching: 2 }));
    listSearches.mockResolvedValueOnce(page([item({ id: 12, query: 'an older one', queryChars: 12 })], { nextBefore: null, matching: 2 }));
    const user = userEvent.setup();
    render(<SearchesView onError={vi.fn()} />);

    await user.click(await screen.findByRole('button', { name: 'Older searches' }));

    expect(await screen.findByText('an older one')).toBeInTheDocument();
    expect(listSearches).toHaveBeenLastCalledWith(expect.objectContaining({ before: 900 }));
    expect(screen.queryByRole('button', { name: 'Older searches' })).not.toBeInTheDocument();
  });

  it('hands a failed read to the error banner', async () => {
    const failure = new Error('refused');
    listSearches.mockRejectedValue(failure);
    const onError = vi.fn();

    render(<SearchesView onError={onError} />);

    await vi.waitFor(() => expect(onError).toHaveBeenCalledWith(failure));
  });
});

describe('what a request for a page carries', () => {
  it('carries only the filters that are set', () => {
    expect(queryFor('all', 'any', { key: '', corpus: '' })).toEqual({ limit: 50 });
    expect(queryFor('nothing', 'mcp', { key: 'hook', corpus: 'docs' }))
      .toEqual({ limit: 50, noResults: true, surface: 'mcp', key: 'hook', corpus: 'docs' });
  });
});

describe('the searches screen, branch by branch', () => {
  it('stops asking for the empty searches when All is chosen again', async () => {
    listSearches.mockResolvedValue(page([item()]));
    const user = userEvent.setup();
    render(<SearchesView onError={vi.fn()} />);
    await screen.findByRole('table');

    await user.click(screen.getByRole('radio', { name: 'Found nothing' }));
    await user.click(screen.getByRole('radio', { name: 'All' }));

    await vi.waitFor(() => expect(listSearches.mock.lastCall?.[0]).not.toHaveProperty('noResults'));
  });

  it('trims the key and the corpus before it asks', async () => {
    listSearches.mockResolvedValue(page([item()]));
    const user = userEvent.setup();
    render(<SearchesView onError={vi.fn()} />);
    await screen.findByRole('table');

    await user.type(screen.getByLabelText('Key name'), '  hook  ');
    await user.type(screen.getByLabelText('Corpus name'), ' docs ');
    await user.click(screen.getByRole('button', { name: 'Filter' }));

    await vi.waitFor(() => expect(listSearches).toHaveBeenLastCalledWith(expect.objectContaining({ key: 'hook', corpus: 'docs' })));
  });

  it('says the log is kept indefinitely when retention is zero', async () => {
    listSearches.mockResolvedValue(page([item()], {
      settings: { enabled: true, storesQueryText: true, retentionDays: 0, dropped: 0 },
    }));

    render(<SearchesView onError={vi.fn()} />);

    expect(await screen.findByText(/Kept indefinitely/)).toBeInTheDocument();
  });

  it('says no search matches when a filter leaves nothing, and not that nothing was recorded', async () => {
    listSearches.mockResolvedValueOnce(page([item()]));
    listSearches.mockResolvedValue(page([], { matching: 0 }));
    const user = userEvent.setup();
    render(<SearchesView onError={vi.fn()} />);
    await screen.findByRole('table');

    await user.click(screen.getByRole('radio', { name: 'Found nothing' }));

    expect(await screen.findByText('No searches match')).toBeInTheDocument();
    expect(screen.queryByText('No searches recorded')).not.toBeInTheDocument();
  });

  it('counts the searches that match, not the rows on the page', async () => {
    listSearches.mockResolvedValue(page([item()], { matching: 7, nextBefore: 3 }));

    render(<SearchesView onError={vi.fn()} />);

    expect(await screen.findByText(/7 searches recorded/)).toBeInTheDocument();
  });

  it('says how far back the log goes', async () => {
    listSearches.mockResolvedValue(page([item()], { oldestUtc: '2026-11-01T09:00:00Z' }));

    render(<SearchesView onError={vi.fn()} />);

    expect(await screen.findByText(/The oldest was/)).toBeInTheDocument();
  });

  it('asks again when Refresh is pressed', async () => {
    listSearches.mockResolvedValue(page([item()]));
    const user = userEvent.setup();
    render(<SearchesView onError={vi.fn()} />);
    await screen.findByRole('table');
    listSearches.mockClear();

    await user.click(screen.getByRole('button', { name: 'Refresh' }));

    await vi.waitFor(() => expect(listSearches).toHaveBeenCalledTimes(1));
  });

  it('keeps the rows it has when it loads older ones, and drops them when a filter changes', async () => {
    listSearches.mockResolvedValueOnce(page([item({ id: 900, query: 'the newest one', queryChars: 14 })], { nextBefore: 900, matching: 3 }));
    listSearches.mockResolvedValueOnce(page([item({ id: 12, query: 'an older one', queryChars: 12 })], { nextBefore: 12, matching: 3 }));
    listSearches.mockResolvedValueOnce(page([item({ id: 3, query: 'the oldest one', queryChars: 14 })], { matching: 3 }));
    const user = userEvent.setup();
    render(<SearchesView onError={vi.fn()} />);

    await user.click(await screen.findByRole('button', { name: 'Older searches' }));
    expect(await screen.findByText('an older one')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Older searches' }));
    expect(await screen.findByText('the oldest one')).toBeInTheDocument();
    expect(screen.getByText('an older one')).toBeInTheDocument();
    expect(screen.getByText('the newest one')).toBeInTheDocument();

    listSearches.mockResolvedValue(page([item({ id: 950, query: 'a filtered one', queryChars: 14 })]));
    await user.click(screen.getByRole('radio', { name: 'Found nothing' }));

    expect(await screen.findByText('a filtered one')).toBeInTheDocument();
    expect(screen.queryByText('an older one')).not.toBeInTheDocument();
    expect(screen.queryByText('the newest one')).not.toBeInTheDocument();
  });

  it('asks for older searches under the filters that are set', async () => {
    listSearches.mockResolvedValueOnce(page([item()]));
    listSearches.mockResolvedValueOnce(page([item({ id: 7 })], { nextBefore: 7, matching: 2 }));
    listSearches.mockResolvedValueOnce(page([item({ id: 3 })], { matching: 2 }));
    const user = userEvent.setup();
    render(<SearchesView onError={vi.fn()} />);
    await screen.findByRole('table');

    await user.click(screen.getByRole('radio', { name: 'Found nothing' }));
    await user.click(await screen.findByRole('button', { name: 'Older searches' }));

    await vi.waitFor(() => expect(listSearches).toHaveBeenLastCalledWith(expect.objectContaining({ noResults: true, before: 7 })));
  });

  it('drops older searches that arrive after the filters changed', async () => {
    let releaseOlder: (p: SearchLogPage) => void = () => {};
    listSearches.mockResolvedValueOnce(page([item({ id: 900 })], { nextBefore: 900, matching: 2 }));
    listSearches.mockReturnValueOnce(new Promise<SearchLogPage>((resolve) => { releaseOlder = resolve; }));
    listSearches.mockResolvedValue(page([item({ id: 950, query: 'under the new filter', queryChars: 20 })]));
    const user = userEvent.setup();
    render(<SearchesView onError={vi.fn()} />);

    await user.click(await screen.findByRole('button', { name: 'Older searches' }));
    await user.click(screen.getByRole('radio', { name: 'Found nothing' }));
    expect(await screen.findByText('under the new filter')).toBeInTheDocument();
    await act(async () => { releaseOlder(page([item({ id: 12, query: 'under the old filter', queryChars: 19 })])); });

    expect(screen.queryByText('under the old filter')).not.toBeInTheDocument();
  });

  it('says indefinitely when retention is below zero too', async () => {
    listSearches.mockResolvedValue(page([item()], {
      settings: { enabled: true, storesQueryText: true, retentionDays: -1, dropped: 0 },
    }));

    render(<SearchesView onError={vi.fn()} />);

    expect(await screen.findByText(/Kept indefinitely/)).toBeInTheDocument();
  });

  it('hands a failed load of older searches to the error banner', async () => {
    const failure = new Error('refused');
    listSearches.mockResolvedValueOnce(page([item()], { nextBefore: 5, matching: 2 }));
    listSearches.mockRejectedValueOnce(failure);
    const onError = vi.fn();
    const user = userEvent.setup();
    render(<SearchesView onError={onError} />);

    await user.click(await screen.findByRole('button', { name: 'Older searches' }));

    await vi.waitFor(() => expect(onError).toHaveBeenCalledWith(failure));
  });

  it('shows the answer to the filter that was last chosen when an earlier answer arrives late', async () => {
    let releaseFirst: (p: SearchLogPage) => void = () => {};
    listSearches.mockReturnValueOnce(new Promise<SearchLogPage>((resolve) => { releaseFirst = resolve; }));
    listSearches.mockResolvedValue(page([item({ query: 'the filtered answer', queryChars: 19 })]));
    const user = userEvent.setup();
    render(<SearchesView onError={vi.fn()} />);

    await user.click(screen.getByRole('radio', { name: 'Found nothing' }));
    expect(await screen.findByText('the filtered answer')).toBeInTheDocument();
    await act(async () => { releaseFirst(page([item({ query: 'the stale answer', queryChars: 16 })])); });

    expect(listSearches).toHaveBeenCalledTimes(2);
    expect(screen.queryByText('the stale answer')).not.toBeInTheDocument();
    expect(screen.getByText('the filtered answer')).toBeInTheDocument();
  });

  it('marks a search that did not name its interface as a defect, and one that did as ordinary', async () => {
    listSearches.mockResolvedValue(page([item({ surface: 'Other' }), item({ surface: 'Mcp' })]));

    render(<SearchesView onError={vi.fn()} />);

    expect((await screen.findByText('Unknown')).className).toMatch(/warn/);
    expect(screen.getByText('MCP tool').className).not.toMatch(/warn/);
  });
});
