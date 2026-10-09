import { describe, expect, it } from 'vitest';
import { gitPathspecs, globList } from './globs';

describe('globList', () => {
  it('splits on commas and newlines and drops the blanks', () => {
    expect(globList('a,b')).toEqual(['a', 'b']);
    expect(globList('a, b\nc,, \n d ')).toEqual(['a', 'b', 'c', 'd']);
    expect(globList('')).toEqual([]);
    expect(globList(' , ,\n')).toEqual([]);
  });

  it('keeps a comma inside pathspec magic in its element', () => {
    expect(globList(':(glob,icase)x')).toEqual([':(glob,icase)x']);
    expect(globList(':(top,exclude)x,c')).toEqual([':(top,exclude)x', 'c']);
    expect(globList(' :(glob,icase)docs/*.md, src/**')).toEqual([':(glob,icase)docs/*.md', 'src/**']);
    expect(globList('a,:(glob,icase)x,b')).toEqual(['a', ':(glob,icase)x', 'b']);
  });

  it('groups only where an element begins with :( and only up to the first )', () => {
    expect(globList('a(b,c)')).toEqual(['a(b', 'c)']);
    expect(globList(':(glob)a,b')).toEqual([':(glob)a', 'b']);
    expect(globList('x:(a,b)')).toEqual(['x:(a', 'b)']);
  });

  it('reads a list shown on one line back as it was stored', () => {
    const stored = [':(glob,icase)docs/*.md', 'src/**', ':(top,exclude)vendor', '/build', 'a b'];

    expect(globList(stored.join(', '))).toEqual(stored);
    expect(globList(stored.join('\n'))).toEqual(stored);
  });

  it('leaves an unclosed magic as one element to the end of the text', () => {
    expect(globList(':(glob,icase')).toEqual([':(glob,icase']);
  });
});

/** The cases of GitHistoryTests.OneLeadingSlashIsTheOnlyThingRemovedFromAPathspecAndNeverWhereItWouldMakeMagic. */
describe('gitPathspecs', () => {
  it.each([
    ['/src', 'src'],
    ['src/', 'src/'],
    ['/*.md', '*.md'],
    ['//src', '//src'],
    ['//', '//'],
    ['/:(exclude)src', '/:(exclude)src'],
    ['/:x', '/:x'],
    [':(glob)src/**', ':(glob)src/**'],
    [':(glob)/src', ':(glob)/src'],
    ['a/../b', 'a/../b'],
    ['\\src', '\\src'],
    ['', ''],
    [' ', ' '],
  ])('gives git %j as %j', (given, passed) => {
    expect(gitPathspecs([given])).toEqual([passed]);
  });

  it('drops an element that is only a slash, and gives no pathspec for a list of them', () => {
    expect(gitPathspecs(['/'])).toEqual([]);
    expect(gitPathspecs(['/', '/'])).toEqual([]);
    expect(gitPathspecs(['/', 'src'])).toEqual(['src']);
    expect(gitPathspecs([])).toEqual([]);
  });
});
