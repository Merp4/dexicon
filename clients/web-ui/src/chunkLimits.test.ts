import { describe, expect, it } from 'vitest';
import { MAX_CHUNK_TOKENS, MIN_CHUNK_TOKENS, suggestedOverlap, usableChunkTokens } from './chunkLimits';

describe('the chunk size suggested for a model', () => {
  it('is the recommendation when a chunk set accepts it', () => {
    expect(usableChunkTokens(665)).toBe(665);
    expect(usableChunkTokens(8192)).toBe(8192);
  });

  it('is never more than a chunk set accepts, however long the model context', () => {
    // 0.9 of qwen3-embedding:0.6b's 32k context is 29,491 tokens; the server refuses anything over 8,192.
    expect(MAX_CHUNK_TOKENS).toBe(8192);
    expect(usableChunkTokens(29_491)).toBe(8192);
    expect(usableChunkTokens(8193)).toBe(8192);
  });

  it('is never less than a chunk set accepts, however small the model context', () => {
    expect(MIN_CHUNK_TOKENS).toBe(64);
    expect(usableChunkTokens(45)).toBe(64);
    expect(usableChunkTokens(64)).toBe(64);
    expect(usableChunkTokens(65)).toBe(65);
  });

  it('has an overlap of an eighth of the size and at least one token', () => {
    expect(suggestedOverlap(8192)).toBe(1024);
    expect(suggestedOverlap(665)).toBe(83);
    expect(suggestedOverlap(1)).toBe(1);
  });

  it('rounds the overlap to the nearest token, halves up', () => {
    // 12 / 8 is 1.5 and 20 / 8 is 2.5: rounding gives 2 and 3, flooring would give 1 and 2.
    expect(suggestedOverlap(12)).toBe(2);
    expect(suggestedOverlap(20)).toBe(3);
  });
});
