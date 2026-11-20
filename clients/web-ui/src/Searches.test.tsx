import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SearchesView, score, whatWasAsked } from './Searches';
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
  it('lists each search with its door, its hits and its time taken', async () => {
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

  it('flags a search that did not name its door', async () => {
    listSearches.mockResolvedValue(page([item({ surface: 'Other' })]));

    render(<SearchesView onError={vi.fn()} />);

    expect(await screen.findByText('Unnamed')).toBeInTheDocument();
  });

  it('says how long the text is kept and that backups hold it', async () => {
    listSearches.mockResolvedValue(page([item()]));

    render(<SearchesView onError={vi.fn()} />);

    expect(await screen.findByText(/Kept for 30 days/)).toBeInTheDocument();
    expect(screen.getByText(/is in backups of the catalogue/)).toBeInTheDocument();
  });

  it('says when no text is stored', async () => {
    listSearches.mockResolvedValue(page([item({ query: null })], {
      settings: { enabled: true, storesQueryText: false, retentionDays: 7, dropped: 0 },
    }));

    render(<SearchesView onError={vi.fn()} />);

    expect(await screen.findByText(/The text of a query is not stored, only its length/)).toBeInTheDocument();
    expect(screen.getByText(/Kept for 7 days/)).toBeInTheDocument();
  });

  it('says so when the log is switched off', async () => {
    listSearches.mockResolvedValue(page([], {
      settings: { enabled: false, storesQueryText: true, retentionDays: 30, dropped: 0 },
    }));

    render(<SearchesView onError={vi.fn()} />);

    expect(await screen.findByText(/The search log is switched off/)).toBeInTheDocument();
    expect(screen.getByText('No searches recorded yet')).toBeInTheDocument();
    expect(screen.getByText('The log is switched off.')).toBeInTheDocument();
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
