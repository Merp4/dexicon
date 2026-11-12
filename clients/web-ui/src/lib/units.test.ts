import { describe, expect, it } from 'vitest';
import type { Corpus } from '../api';
import { unitFor } from './units';

type Sources = Corpus['sources'];

const files = { kind: 'workspace' } as Sources[number];
const commits = { kind: 'githistory', git: { includeStashes: false } } as Sources[number];
const withStashes = { kind: 'githistory', git: { includeStashes: true } } as Sources[number];

describe('unitFor', () => {
  it('counts files for a corpus with no history source', () => {
    expect(unitFor([files], 2)).toBe('files');
    expect(unitFor(undefined, 1)).toBe('file');
  });

  it('counts commits for a corpus of history alone, and documents once another kind is mixed in', () => {
    expect(unitFor([commits], 201)).toBe('commits');
    expect(unitFor([commits], 1)).toBe('commit');
    expect(unitFor([files, commits], 5)).toBe('documents');
  });

  it('counts documents for a history source that indexes stashes, which hold more than commits', () => {
    expect(unitFor([withStashes], 4)).toBe('documents');
    expect(unitFor([withStashes], 1)).toBe('document');
  });
});
