import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SearchView } from './App';
import { ApiError } from './api';
import type { Corpus, SearchResult } from './api';

/**
 * The search screen, which is the product.
 *
 * Two things it has to do beyond returning rows. It has to make "the index is bad"
 * distinguishable from "the scope was wrong", which is what Explain is for. And it has to
 * survive a failed request: a call that rejects used to take the page with it, because the
 * facade resolved `undefined` into state and the next `.map` threw. That fault is fixed in
 * api.ts, but no component test ever asserted the screen's half of it.
 */
const search = vi.fn();

vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api')>();
  return { ...actual, api: { search: (...a: unknown[]) => search(...a) } };
});

const corpora = [{ id: 'c1', name: 'docs' }, { id: 'c2', name: 'api-repo' }] as Corpus[];

function result(over: Partial<SearchResult> = {}): SearchResult {
  return {
    query: 'chunk sets',
    mode: 'Hybrid',
    degraded: false,
    degradedReason: null,
    scope: [{ id: 'c1', name: 'docs', state: 'Ready' }],
    hits: [
      {
        corpusId: 'c1',
        corpusName: 'docs',
        filePath: '04-ingestion.md',
        startLine: 228,
        endLine: 265,
        content: 'Chunk size, overlap, boundary mode and the embedding model belong to a chunk set.',
        score: 0.8,
        location: '04-ingestion.md:228-265',
        symbols: [],
      },
    ],
    tookMs: 257,
    note: null,
    ...over,
  } as unknown as SearchResult;
}

async function searchFor(text: string) {
  const user = userEvent.setup();
  render(<SearchView corpora={corpora} onError={vi.fn()} />);

  await user.type(screen.getByPlaceholderText(/Ask a question/), text);
  await user.click(screen.getByRole('button', { name: 'Search' }));
  return user;
}

beforeEach(() => {
  vi.clearAllMocks();
  search.mockResolvedValue(result());
});

describe('searching', () => {
  it('shows where a hit is, not just what it says', async () => {
    // `file:line-line` is what gets pasted back into a conversation or an editor.
    await searchFor('chunk sets');

    expect(await screen.findByText('04-ingestion.md:228-265')).toBeInTheDocument();
  });

  it('sends the query, the mode and the limit', async () => {
    await searchFor('chunk sets');

    await waitFor(() => expect(search).toHaveBeenCalled());
    expect(search).toHaveBeenCalledWith(
      expect.objectContaining({ query: 'chunk sets', mode: 'hybrid', limit: 10 }),
    );
  });

  it('searches everything visible unless a corpus is chosen', async () => {
    // The scope sentinel is a UI concept. It must not reach the server, where it would be
    // a corpus name that does not exist.
    await searchFor('chunk sets');

    await waitFor(() => expect(search).toHaveBeenCalled());
    expect(search.mock.calls[0][0].corpus).toBeUndefined();
  });

  it('will not search for nothing', async () => {
    render(<SearchView corpora={corpora} onError={vi.fn()} />);

    expect(screen.getByRole('button', { name: 'Search' })).toBeDisabled();
    expect(search).not.toHaveBeenCalled();
  });

  it('submits on Enter, because the box asks a question', async () => {
    const user = userEvent.setup();
    render(<SearchView corpora={corpora} onError={vi.fn()} />);

    await user.type(screen.getByPlaceholderText(/Ask a question/), 'chunk sets{Enter}');

    await waitFor(() => expect(search).toHaveBeenCalledOnce());
  });
});

describe('telling a bad index from a bad scope', () => {
  it('names the scope that was actually searched', async () => {
    // The single most useful thing this screen can show: you asked for everything and got
    // one corpus, or you asked for one and got it.
    const user = await searchFor('chunk sets');

    await user.click(await screen.findByRole('button', { name: 'Explain' }));

    expect(screen.getByText(/resolved scope:/)).toBeInTheDocument();
    expect(screen.getByText('docs', { selector: 'div' })).toBeInTheDocument();
  });

  it('says when the mode it used was not the mode asked for', async () => {
    // Keyword results that look like hybrid results are a lie by omission: the absence of
    // a semantic match is not the absence of the content.
    search.mockResolvedValue(
      result({ mode: 'Keyword', degraded: true, degradedReason: 'Embeddings unavailable.' }),
    );

    const user = await searchFor('chunk sets');

    expect(await screen.findByText(/Embeddings unavailable/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Explain' }));
    expect(screen.getByText(/degraded from requested/)).toBeInTheDocument();
  });

  it('points an empty result at Jobs rather than implying the content is absent', async () => {
    search.mockResolvedValue(result({ hits: [] }));

    await searchFor('chunk sets');

    expect(await screen.findByText('Nothing matched')).toBeInTheDocument();
    expect(screen.getByText(/may still be indexing/)).toBeInTheDocument();
  });

  it('passes on a mid-index warning instead of quietly returning less', async () => {
    search.mockResolvedValue(result({ note: 'docs is still indexing; results are incomplete.' }));

    await searchFor('chunk sets');

    expect(await screen.findByText(/still indexing; results are incomplete/)).toBeInTheDocument();
  });
});

describe('when the request fails', () => {
  it('reports it rather than taking the screen down', async () => {
    // The regression that blanked the page: a rejected call resolved to undefined, went
    // into state, and the next `.map` threw. This is the screen's half of that.
    const onError = vi.fn();
    search.mockRejectedValue(new ApiError(404, 'Unknown corpus', "Unknown corpus 'api'."));

    const user = userEvent.setup();
    render(<SearchView corpora={corpora} onError={onError} />);

    await user.type(screen.getByPlaceholderText(/Ask a question/), 'chunk sets');
    await user.click(screen.getByRole('button', { name: 'Search' }));

    await waitFor(() => expect(onError).toHaveBeenCalled());

    // The server's own message, which is written for a person to act on.
    expect(String(onError.mock.calls[0][0])).toContain("Unknown corpus 'api'.");
    // And the screen is still a search screen.
    expect(screen.getByRole('button', { name: 'Search' })).toBeInTheDocument();
  });

  it('lets you search again afterwards', async () => {
    // A failed search that leaves the button spinning forever is its own outage.
    search.mockRejectedValueOnce(new ApiError(503, 'Unavailable', 'Qdrant is unreachable.'));

    const user = userEvent.setup();
    render(<SearchView corpora={corpora} onError={vi.fn()} />);

    await user.type(screen.getByPlaceholderText(/Ask a question/), 'chunk sets');
    await user.click(screen.getByRole('button', { name: 'Search' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Search' })).toBeEnabled());

    search.mockResolvedValue(result());
    await user.click(screen.getByRole('button', { name: 'Search' }));

    expect(await screen.findByText('04-ingestion.md:228-265')).toBeInTheDocument();
  });
});
