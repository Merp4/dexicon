import type { Corpus } from '../api';

/**
 * What a count is counting, singular or plural.
 *
 * A git-history source's units are commits, so a corpus made only of them reporting
 * "201 files" contradicts the source row directly beneath it. A corpus holding both
 * kinds is counting two different things at once and neither word is true of the total,
 * so it says "documents" — the word docs/04 already uses for the unit a chunk comes
 * from.
 *
 * A corpus with no history source is unchanged, which is almost all of them.
 *
 * It lives here rather than in `App.tsx` because `ChunkSets.tsx` needs it too, and
 * importing it from there would close a cycle.
 */
export function unitFor(sources: Corpus['sources'] | undefined, n: number): string {
  const kinds = new Set((sources ?? []).map((s) => s.kind));
  const one = !kinds.has('githistory') ? 'file'
    : kinds.size === 1 ? 'commit'
    : 'document';

  return n === 1 ? one : `${one}s`;
}

/** The same, for one source, where the kind is known exactly. */
export function unitOf(s: Corpus['sources'][number], n: number): string {
  return unitFor([s], n);
}
