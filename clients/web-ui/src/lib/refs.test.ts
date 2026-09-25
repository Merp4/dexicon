import { describe, expect, it } from 'vitest';
import type { GitRefListing, GitUpstream } from '../api';
import { distance, isStale, listedRef, refLabel } from './refs';

const upstream = (over: Partial<GitUpstream> = {}): GitUpstream =>
  ({ name: 'refs/remotes/origin/main', shortName: 'origin/main', ahead: 0, behind: 0, gone: false, ...over });

describe('a ref, as a person reads it', () => {
  it('drops the prefix and marks a prefetched copy, for both prefetch layouts', () => {
    expect(refLabel('refs/heads/main')).toBe('main');
    expect(refLabel('refs/remotes/origin/main')).toBe('origin/main');
    expect(refLabel('refs/prefetch/origin/main')).toBe('origin/main (prefetched)');
    expect(refLabel('refs/prefetch/remotes/origin/main')).toBe('origin/main (prefetched)');
    expect(refLabel('HEAD')).toBe('HEAD');
    expect(refLabel('v1.2.0')).toBe('v1.2.0');
  });

  it('says how far a branch is from its upstream, and nothing it could not read', () => {
    expect(distance(upstream({ behind: 52 }))).toBe('52 behind origin/main');
    expect(distance(upstream({ ahead: 2 }))).toBe('2 ahead of origin/main');
    expect(distance(upstream({ ahead: 1, behind: 3 }))).toBe('1 ahead and 3 behind origin/main');
    expect(distance(upstream())).toBe('up to date with origin/main');
    expect(distance(upstream({ gone: true, ahead: null, behind: null }))).toBe('origin/main no longer exists');
    expect(distance(upstream({ ahead: null, behind: null }))).toBeNull();
  });

  it('calls behind and gone stale, and ahead alone local work', () => {
    expect(isStale(upstream({ behind: 1 }))).toBe(true);
    expect(isStale(upstream({ gone: true }))).toBe(true);
    expect(isStale(upstream({ ahead: 3 }))).toBe(false);
    expect(isStale(null)).toBe(false);
  });
});

describe('the listed ref a stored value names', () => {
  const at = new Date().toISOString();
  const listing: GitRefListing = {
    head: { branch: 'refs/heads/main', sha: 'a'.repeat(40) },
    local: { truncated: false, refs: [{ name: 'refs/heads/main', shortName: 'main', sha: 'a'.repeat(40), committedUtc: at }] },
    remoteTracking: { truncated: false, refs: [{ name: 'refs/remotes/origin/main', shortName: 'origin/main', sha: 'b'.repeat(40), committedUtc: at }] },
    prefetched: { truncated: false, refs: [{ name: 'refs/prefetch/origin/main', shortName: 'prefetch/origin/main', sha: 'c'.repeat(40), committedUtc: at }] },
    lastFetchUtc: null,
  };

  it('matches a full name exactly, in any group', () => {
    expect(listedRef(listing, 'refs/remotes/origin/main')?.kind).toBe('remote');
    expect(listedRef(listing, 'refs/prefetch/origin/main')?.kind).toBe('prefetched');
  });

  it('matches a short name as a branch, then a remote-tracking ref', () => {
    expect(listedRef(listing, 'main')?.ref.name).toBe('refs/heads/main');
    expect(listedRef(listing, 'origin/main')?.ref.name).toBe('refs/remotes/origin/main');
    expect(listedRef(listing, 'v1.2.0')).toBeNull();
    expect(listedRef(listing, 'HEAD')).toBeNull();
  });
});
