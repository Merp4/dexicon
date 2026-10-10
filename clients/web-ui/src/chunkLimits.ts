/**
 * The most tokens a chunk set accepts: ChunkSettingRules.MaxChunkSize on the server, which answers 400 for
 * more. A model with a long context is recommended at most this much by the server, and a server that has
 * not been updated, or a measurement stored before the cap, can still send more.
 */
export const MAX_CHUNK_TOKENS = 8192;

/** The smallest size a chunk set accepts: ChunkSettingRules.MinChunkSize on the server. */
export const MIN_CHUNK_TOKENS = 64;

/**
 * The size to send or suggest for a model's recommendation: the recommendation, held to the range a chunk
 * set accepts, so a model with a context of a few dozen tokens is not offered a size the server refuses.
 */
export function usableChunkTokens(recommended: number): number {
  return Math.min(Math.max(recommended, MIN_CHUNK_TOKENS), MAX_CHUNK_TOKENS);
}

/** An eighth of the size, at least one token: the overlap the server's default keeps to the size. */
export function suggestedOverlap(size: number): number {
  return Math.max(1, Math.round(size / 8));
}
