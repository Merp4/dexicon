/**
 * Which screen the URL is pointing at.
 *
 * In the hash, not the path. The app is served by the same origin that serves the API, and
 * every path but `/` needs a token: a real path would 401 on reload, which is the failure
 * this exists to fix. Widening the anonymous list to cover the SPA's routes would trade a
 * security surface for a prettier URL, and the browser never asks the server for a fragment.
 *
 * Reload, bookmark, and the back button all work from this. Nothing else is kept: a search
 * query or a scroll position in the URL is a promise to restore state that is not actually
 * restored.
 */

export const VIEWS = [
  'search', 'corpora', 'documents', 'jobs', 'models', 'access', 'settings',
] as const;

export type View = (typeof VIEWS)[number];

export interface Route {
  view: View;
  /** The corpus whose detail page is open. Only meaningful on `corpora`. */
  corpus?: string;
}

const DEFAULT: Route = { view: 'search' };

/**
 * Parse a location hash. Anything unrecognised is the default screen rather than an error:
 * a hand-edited or stale URL should land somewhere useful, not on a blank page.
 */
export function parseHash(hash: string): Route {
  const parts = hash.replace(/^#\/?/, '').split('/').filter(Boolean).map(decodeURIComponent);
  const [view, ...rest] = parts;

  if (!view || !(VIEWS as readonly string[]).includes(view)) return DEFAULT;

  // A corpus name can contain anything a name can, so the remainder is joined back rather
  // than assumed to be one segment.
  const corpus = rest.join('/');

  return view === 'corpora' && corpus ? { view: 'corpora', corpus } : { view: view as View };
}

/**
 * The hash for a route. Encoded per segment, so a corpus with a slash or a space in its
 * name survives the round trip.
 */
export function toHash(route: Route): string {
  const parts: string[] = [route.view];
  if (route.view === 'corpora' && route.corpus) parts.push(route.corpus);

  return `#/${parts.map(encodeURIComponent).join('/')}`;
}
