import type { GitRef, GitRefListing, GitUpstream } from '../api';

/**
 * A ref as a person reads it. A picked ref is stored in full, `refs/heads/main`, because a
 * short name can change meaning: git resolves a tag before a branch, so a tag called
 * `main` would take over a source following `main` without anything saying so.
 */
export function refLabel(ref: string): string {
  if (ref.startsWith('refs/heads/')) return ref.slice('refs/heads/'.length);
  if (ref.startsWith('refs/remotes/')) return ref.slice('refs/remotes/'.length);
  if (ref.startsWith('refs/prefetch/'))
    return `${ref.slice('refs/prefetch/'.length).replace(/^remotes\//, '')} (prefetched)`;
  return ref;
}

export type RefKind = 'local' | 'remote' | 'prefetched';

/**
 * The listed ref a stored value names. A full name matches exactly. A short name, as a
 * source saved before the picker stored one, matches the ref the server resolved it to
 * when the listing was asked about it, and nothing otherwise: git resolves a tag before a
 * branch, and the listing holds no tags, so a short name's spelling cannot say which
 * listed ref it is.
 */
export function listedRef(listing: GitRefListing, value: string): { kind: RefKind; ref: GitRef } | null {
  const name = value === listing.followed?.ref ? listing.followed.name ?? value : value;

  const groups: [RefKind, GitRef[]][] = [
    ['local', listing.local.refs],
    ['remote', listing.remoteTracking.refs],
    ['prefetched', listing.prefetched.refs],
  ];
  for (const [kind, refs] of groups) {
    const ref = refs.find((r) => r.name === name);
    if (ref) return { kind, ref };
  }
  return null;
}

/**
 * How far a branch is from its upstream, in words: "52 behind origin/main". Null when
 * git's own words could not be read, which the server reports as no count rather than a
 * guessed one.
 */
export function distance(upstream: GitUpstream): string | null {
  if (upstream.gone) return `${upstream.shortName} no longer exists`;
  const { ahead, behind } = upstream;
  if (ahead == null || behind == null) return null;
  if (ahead === 0 && behind === 0) return `up to date with ${upstream.shortName}`;
  if (ahead === 0) return `${behind.toLocaleString()} behind ${upstream.shortName}`;
  if (behind === 0) return `${ahead.toLocaleString()} ahead of ${upstream.shortName}`;
  return `${ahead.toLocaleString()} ahead and ${behind.toLocaleString()} behind ${upstream.shortName}`;
}

/** Behind, or gone: the states a person should act on. Ahead alone is local work, not staleness. */
export function isStale(upstream: GitUpstream | null | undefined): boolean {
  return !!upstream && (upstream.gone || (upstream.behind ?? 0) > 0);
}
