import type { Corpus } from '../api';

/**
 * What to call a source on screen: its path, or its kind where it has none.
 *
 * An empty path is the workspace root rather than a missing one. `rootPath ?? kind`
 * let the empty string through, so a source at the root rendered with no name, and its
 * buttons were labelled "Remove source " with nothing after it.
 */
export function sourceName(s: Corpus['sources'][number]): string {
  if (s.rootPath === '') return 'workspace root';
  return s.rootPath ?? s.kind;
}
