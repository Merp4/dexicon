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

/**
 * What each commit document of a history source holds, in words for its row.
 *
 * From all three settings, and the server's defaults where one is absent. It was read
 * off the diff alone, "with the diff" or "message and stat", which went wrong as soon as
 * the message or the stat could be turned off.
 */
export function historyContent(git: Corpus['sources'][number]['git']): string {
  const parts = [
    (git?.includeMessage ?? true) && 'message',
    (git?.includeStat ?? true) && 'stat',
    (git?.includeDiff ?? false) && 'diff',
  ].filter((p): p is string => typeof p === 'string');

  if (parts.length === 0) return 'sha, author and date only';
  if (parts.length === 1) return `${parts[0]} only`;
  return `${parts.slice(0, -1).join(', ')} and ${parts[parts.length - 1]}`;
}
