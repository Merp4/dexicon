import { describe, expect, it } from 'vitest';
import { parseHash, toHash, VIEWS } from './route';

/**
 * Reloading the page used to land on Search, whatever you were looking at, because the app
 * kept no view state in the URL. So did a bookmark, and the back button did nothing at all.
 *
 * The hash carries it. Not the path: every path but `/` needs a token, so a real path would
 * 401 on the very reload this is meant to fix, and widening the anonymous list would trade a
 * security surface for a prettier URL.
 */
describe('the screen in the URL', () => {
  it('round-trips every view', () => {
    for (const view of VIEWS) {
      expect(parseHash(toHash({ view }))).toEqual({ view });
    }
  });

  it('round-trips an open corpus', () => {
    const route = { view: 'corpora' as const, corpus: 'books' };
    expect(parseHash(toHash(route))).toEqual(route);
  });

  it('survives a corpus name that needs encoding', () => {
    // Names are free text. A slash would otherwise read as another path segment, and a
    // space would break the URL outright.
    for (const corpus of ['my books', 'a/b', 'q&a', 'héllo', '100% docs']) {
      expect(parseHash(toHash({ view: 'corpora', corpus }))).toEqual({ view: 'corpora', corpus });
    }
  });

  it('lands somewhere useful rather than nowhere', () => {
    // Hand-edited, stale, or from an older version of the app. A blank screen would be the
    // worst of the available answers.
    for (const hash of ['', '#', '#/', '#/nonsense', '#/search/extra', 'garbage']) {
      expect(parseHash(hash).view).toBe('search');
    }
  });

  it('ignores a corpus on a view that has no corpus', () => {
    // `#/jobs/books` is not a screen. Keeping the name would leave state pointing at
    // something the view cannot show.
    expect(parseHash('#/jobs/books')).toEqual({ view: 'jobs' });
  });

  it('reads a hash with or without the leading slash', () => {
    expect(parseHash('#/corpora')).toEqual({ view: 'corpora' });
    expect(parseHash('#corpora')).toEqual({ view: 'corpora' });
  });

  it('writes no corpus segment when none is open', () => {
    // Otherwise the list and the detail page share a URL, and the back button cannot tell
    // them apart.
    expect(toHash({ view: 'corpora' })).toBe('#/corpora');
    expect(toHash({ view: 'corpora', corpus: 'books' })).toBe('#/corpora/books');
  });
});
