import { describe, expect, it } from 'vitest';
import encoderSource from '../../../../src/Dexicon.Core/Search/SparseEncoder.cs?raw';
import {
  ENCODER_STOPWORDS, MAX_TERM_LENGTH, MIN_TERM_LENGTH, identifierParts, queryTerms, splitOnTerms,
} from './terms';

/** The marked segments, which `splitOnTerms` puts at the odd indices. */
const marked = (text: string, query: string) => splitOnTerms(text, query).filter((_, i) => i % 2 === 1);

describe('the rules the keyword index uses', () => {
  // Read from the encoder itself, so a change on one side without the other fails here
  // rather than showing up as a hit with nothing marked.
  const stopBlock = encoderSource.match(/StopWords\s*=\s*new\([^)]*\)\s*\{([^}]*)\}/)?.[1] ?? '';
  const serverStopwords = [...stopBlock.matchAll(/"([^"]+)"/g)].map((m) => m[1]);
  const serverMin = Number(encoderSource.match(/MinTermLength\s*=\s*(\d+)/)?.[1]);
  const serverMax = Number(encoderSource.match(/MaxTermLength\s*=\s*(\d+)/)?.[1]);

  it('are read from the encoder source', () => {
    // A parse that found nothing would make the comparisons below pass vacuously.
    expect(serverStopwords.length).toBeGreaterThan(20);
    expect(serverMin).toBeGreaterThan(0);
    expect(serverMax).toBeGreaterThan(serverMin);
  });

  it('keep the same stopwords', () => {
    expect([...ENCODER_STOPWORDS].sort()).toEqual([...serverStopwords].sort());
  });

  it('keep the same shortest and longest term', () => {
    expect(MIN_TERM_LENGTH).toBe(serverMin);
    expect(MAX_TERM_LENGTH).toBe(serverMax);
  });
});

describe('identifier parts', () => {
  it('split where the encoder splits', () => {
    expect(identifierParts('RefreshAsync')).toEqual(['Refresh', 'Async']);
    expect(identifierParts('HTTPServer')).toEqual(['HTTP', 'Server']);
    expect(identifierParts('base64Encode')).toEqual(['base', '64', 'Encode']);
    expect(identifierParts('plain')).toEqual(['plain']);
  });
});

describe('the terms a query marks', () => {
  it('keeps a two-letter term, as the index does', () => {
    // "ID" is a term to the encoder, and a hit on it had nothing marked.
    expect(queryTerms('ID')).toEqual(new Set(['id']));
    expect(marked('the userId and the id column', 'ID')).toEqual(['Id', 'id']);
  });

  it('marks a common word that is part of an identifier', () => {
    // `NotFound` is indexed as "not" and "found" too; "not" is only a prose stopword.
    expect(marked('Returns NotFound when it is not found', 'NotFound')).toEqual(['NotFound', 'not', 'found']);
  });

  it('does not mark a common word the query has on its own', () => {
    expect(marked('not every passage, only this one', 'not passage')).toEqual(['passage']);
  });

  it('reads an underscore compound as its runs', () => {
    expect(marked('corpus_id, corpus and not_found', 'corpus_id not_found')).toEqual(['corpus_id', 'corpus', 'not_found']);
  });

  it('drops the encoder stopwords everywhere', () => {
    expect(queryTerms('is IsReady')).toEqual(new Set(['isready', 'ready']));
  });
});
