import { render, screen, waitFor, within } from '@testing-library/react';
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
const fileText = vi.fn();

vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api')>();
  return {
    ...actual,
    api: {
      search: (...a: unknown[]) => search(...a),
      fileText: (...a: unknown[]) => fileText(...a),
    },
  };
});

const corpora = [{ id: 'c1', name: 'docs' }, { id: 'c2', name: 'api-repo' }] as Corpus[];

type Hit = SearchResult['hits'][number];

function hit(over: Partial<Hit> = {}): Hit {
  return {
    corpusId: 'c1',
    corpusName: 'docs',
    filePath: '04-ingestion.md',
    startLine: 228,
    endLine: 265,
    content: 'Chunk size, overlap, boundary mode and the embedding model belong to a chunk set.',
    score: 0.8,
    location: '04-ingestion.md:228-265',
    symbols: [],
    ...over,
  } as unknown as Hit;
}

function result(over: Partial<SearchResult> = {}): SearchResult {
  return {
    query: 'chunk sets',
    mode: 'Hybrid',
    degraded: false,
    degradedReason: null,
    scope: [{ id: 'c1', name: 'docs', state: 'Ready' }],
    hits: [hit()],
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
  // clearAllMocks keeps queued Once answers, so one a test did not use reached the next.
  fileText.mockReset();
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

describe('marking why a hit matched', () => {
  it('marks the query terms in the passage', async () => {
    // A hit is up to 1,500 characters. Returning it unmarked hands over the haystack and
    // the assurance that the needle is in it.
    search.mockResolvedValue(result({ query: 'overlap boundary' }));
    await searchFor('overlap boundary');

    const marked = (await screen.findAllByText('overlap')).map((n) => n.tagName);
    expect(marked).toContain('MARK');
    expect(screen.getAllByText('boundary').map((n) => n.tagName)).toContain('MARK');
  });

  it('does not mark the stopwords', async () => {
    // "chunking strategy and overlap size" over the books corpus produced 133 marks, 47 of
    // them "and". Marking a word that is in every passage marks the passage.
    search.mockResolvedValue(result({ query: 'overlap and the boundary' }));
    await searchFor('overlap and the boundary');

    const marks = document.querySelectorAll('mark');
    expect(marks.length).toBeGreaterThan(0);
    expect([...marks].map((m) => m.textContent?.toLowerCase())).not.toContain('and');
    expect([...marks].map((m) => m.textContent?.toLowerCase())).not.toContain('the');
  });

  it('marks the parts of an identifier the keyword index matched on', async () => {
    // The sparse encoder indexes `RefreshAsync` as itself and as "refresh" and "async",
    // so a passage that only says "refresh" is a match. Marking whole query words left
    // nine keyword hits of ten for an identifier with nothing marked.
    search.mockResolvedValue(result({
      query: 'RefreshAsync HTTPServer',
      hits: [hit({ content: 'Call refresh before the token expires. The server retries RefreshAsync once.' })],
    }));
    await searchFor('RefreshAsync HTTPServer');
    await screen.findByText('04-ingestion.md:228-265');

    const marks = [...document.querySelectorAll('mark')].map((m) => m.textContent);
    expect(marks).toEqual(['refresh', 'server', 'RefreshAsync']);
  });

  it('marks a term where the index would match it, not inside another word', async () => {
    // "set" is a part of `ChunkSet`, and it was marked inside "settings" and "reset". The
    // index reads each of those as one token, so neither is a match.
    search.mockResolvedValue(result({
      query: 'ChunkSet',
      hits: [hit({ content: 'Settings reset the default set; a ChunkSet and chunk_set too.' })],
    }));
    await searchFor('ChunkSet');
    await screen.findByText('04-ingestion.md:228-265');

    const marks = [...document.querySelectorAll('mark')].map((m) => m.textContent);
    expect(marks).toEqual(['set', 'ChunkSet', 'chunk', 'set']);
  });

  it('sets prose in the body face and code in monospace', async () => {
    // `pre` is monospace in the UA stylesheet, so the prose branch has to say font-sans or
    // a page of a book arrives in 14px monospace regardless of the class it was given.
    search.mockResolvedValue(result({ hits: [hit({ language: 'markdown' })] }));
    await searchFor('chunk sets');

    await screen.findByText('04-ingestion.md:228-265');
    const prose = document.querySelector('pre')!;
    expect(prose.className).toMatch(/font-sans/);
    expect(prose.className).not.toMatch(/\bmono\b/);
  });

  it('keeps monospace for a code chunk', async () => {
    search.mockResolvedValue(result({ hits: [hit({ language: 'csharp' })] }));
    await searchFor('chunk sets');

    await screen.findByText('04-ingestion.md:228-265');
    const code = document.querySelector('pre')!;
    expect(code.className).toMatch(/\bmono\b/);
    expect(code.className).not.toMatch(/font-sans/);
  });
});

describe('opening a hit', () => {
  it('marks every line of the passage, not only its first', async () => {
    fileText.mockResolvedValue({
      corpus: 'docs',
      path: '04-ingestion.md',
      chunkSet: 'default',
      startLine: 226,
      endLine: 268,
      text: Array.from({ length: 43 }, (_, i) => `line ${226 + i}`).join('\n'),
      gaps: 0,
      totalChars: 400,
      nextOffset: null,
    });
    const user = await searchFor('chunk sets');

    await user.click(await screen.findByRole('button', { name: 'Open' }));
    const dialog = await screen.findByRole('dialog');
    await within(dialog).findByText('line 226');

    const marked = [...dialog.querySelectorAll('pre > span')]
      .filter((s) => s.className.includes('accent-soft'))
      .map((s) => s.lastChild?.textContent);
    expect(marked[0]).toBe('line 228');
    expect(marked.at(-1)).toBe('line 265');
    expect(marked).toHaveLength(265 - 228 + 1);
  });
});

describe('opening a hit past the first window', () => {
  it('reads on until the hit is in, then marks it', async () => {
    // The file endpoint answers with a window of 400,000 characters, so a hit late in a
    // book was in none of what the viewer loaded.
    const window = (startLine: number, lines: number, nextOffset: number | null) => ({
      corpus: 'docs', path: 'book.pdf', chunkSet: 'default', gaps: 0, totalChars: 900,
      startLine, endLine: startLine + lines, nextOffset,
      text: Array.from({ length: lines }, (_, i) => `line ${startLine + i}`).join('\n') + '\n',
    });
    fileText
      .mockResolvedValueOnce(window(1, 3, 300))
      .mockResolvedValueOnce(window(4, 3, 600))
      .mockResolvedValueOnce(window(7, 3, null));
    search.mockResolvedValue(result({ hits: [hit({ filePath: 'book.pdf', startLine: 5, endLine: 6 })] }));
    const user = await searchFor('chunk sets');

    await user.click(await screen.findByRole('button', { name: 'Open' }));
    const dialog = await screen.findByRole('dialog');
    await within(dialog).findByText('line 5');

    expect(fileText.mock.calls.map((c) => c[2])).toEqual([undefined, 300]);
    const marked = [...dialog.querySelectorAll('pre > span')]
      .filter((s) => s.className.includes('accent-soft'))
      .map((s) => s.lastChild?.textContent);
    expect(marked).toEqual(['line 5', 'line 6']);
  });

  it('reads on for a hit on the line a window ends before', async () => {
    // A window ending on a newline reports the next line as its endLine, with none of
    // that line's text in it.
    const window = (startLine: number, lines: number, nextOffset: number | null) => ({
      corpus: 'docs', path: 'book.pdf', chunkSet: 'default', gaps: 0, totalChars: 600,
      startLine, endLine: startLine + lines, nextOffset,
      text: Array.from({ length: lines }, (_, i) => `line ${startLine + i}`).join('\n') + '\n',
    });
    fileText
      .mockResolvedValueOnce(window(1, 3, 300))
      .mockResolvedValueOnce(window(4, 3, null));
    search.mockResolvedValue(result({ hits: [hit({ filePath: 'book.pdf', startLine: 4, endLine: 4 })] }));
    const user = await searchFor('chunk sets');

    await user.click(await screen.findByRole('button', { name: 'Open' }));
    const dialog = await screen.findByRole('dialog');
    await within(dialog).findByText('line 1');

    expect(fileText).toHaveBeenCalledTimes(2);
    const marked = [...dialog.querySelectorAll('pre > span')]
      .filter((s) => s.className.includes('accent-soft'))
      .map((s) => s.lastChild?.textContent);
    expect(marked).toEqual(['line 4']);
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

  it('describes the scoring of the mode that ran', async () => {
    // Hybrid fuses with DBSF (D-06). This said "reciprocal rank fusion, k=2. Ordering is
    // meaningful, magnitude is not" after the server stopped doing that, and said it of
    // every mode, so a keyword search was described as a fusion of two lists.
    const user = await searchFor('chunk sets');
    await user.click(await screen.findByRole('button', { name: 'Explain' }));
    expect(screen.getByText(/scores:/).parentElement).toHaveTextContent(/DBSF/);
    expect(screen.queryByText(/reciprocal rank/)).not.toBeInTheDocument();
  });

  it('shows the score Explain describes on each hit', async () => {
    search.mockResolvedValue(result({ hits: [hit({ score: 0.8 }), hit({ score: 0.4126, filePath: 'b.md', location: 'b.md:1-9' })] }));
    await searchFor('chunk sets');

    expect(await screen.findByText('0.800')).toHaveAttribute('title', expect.stringMatching(/DBSF/));
    expect(screen.getByText('0.413')).toBeInTheDocument();
  });

  it('describes a degraded search by the keyword scoring it fell back to', async () => {
    search.mockResolvedValue(result({ mode: 'Keyword', degraded: true, degradedReason: 'Embeddings unavailable.' }));
    const user = await searchFor('chunk sets');
    await user.click(await screen.findByRole('button', { name: 'Explain' }));
    expect(screen.getByText(/scores:/).parentElement).toHaveTextContent(/IDF/);
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

  it('says an incomplete result is a warning, not a label', async () => {
    // It was drawn in the accent blue — the same blue that badges the default chunk set
    // and the corpus a hit came from — so the one line on the screen saying the results
    // could not be trusted read as decoration. It is also announced: a warning that only
    // exists visually never reaches anyone driving this by keyboard.
    search.mockResolvedValue(result({ note: 'docs is still indexing; results are incomplete.' }));
    await searchFor('chunk sets');

    const banner = await screen.findByRole('status');
    expect(banner).toHaveTextContent('still indexing; results are incomplete');
    expect(banner.className).toMatch(/--warn/);
    expect(banner.className).not.toMatch(/--accent/);
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
