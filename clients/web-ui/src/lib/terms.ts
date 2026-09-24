/**
 * Why a search hit matched, read the way the keyword index reads text.
 *
 * The index is `SparseEncoder` on the server: it takes each run of letters and digits,
 * lowercases it, adds the parts of a compound identifier, and drops terms outside
 * `MinTermLength`..`MaxTermLength` and its `StopWords`. `terms.test.ts` reads that file and
 * fails if the constants here stop matching it. The one departure, the prose stopwords, is
 * stated where it is declared.
 */

/** `SparseEncoder.MinTermLength`. */
export const MIN_TERM_LENGTH = 2;

/** `SparseEncoder.MaxTermLength`. */
export const MAX_TERM_LENGTH = 64;

/** `SparseEncoder.StopWords`. Short on purpose: "for", "in" and "is" often name code. */
export const ENCODER_STOPWORDS = new Set([
  'the', 'a', 'an', 'and', 'or', 'of', 'to', 'in', 'on', 'at', 'by', 'is', 'are',
  'was', 'were', 'be', 'been', 'it', 'its', 'this', 'that', 'with', 'as', 'from',
  'how', 'what', 'why', 'we', 'you', 'i', 'do', 'does', 'did',
]);

/**
 * Common words not marked when the query has them as words of its own, though the index
 * does match them.
 *
 * The exception to mirroring the encoder, and a deliberate one: a word in nearly every
 * passage says nothing about why this passage matched. "chunking strategy and overlap
 * size" over the books corpus produced 133 marks, 47 of them the word "and". It does not
 * apply to the parts of an identifier, where the index's own list is the rule, so `NotFound`
 * still marks "not".
 */
const PROSE_STOPWORDS = new Set([
  'for', 'but', 'not', 'your', 'all', 'any', 'can', 'has', 'had', 'our', 'out', 'own',
  'who', 'they', 'them', 'their', 'there', 'then', 'than', 'have', 'when', 'where',
  'which', 'while', 'will', 'would', 'should', 'about', 'into', 'over', 'some', 'such',
  'only', 'other', 'being', 'each', 'more', 'most', 'much', 'very', 'just', 'also', 'here',
]);

const indexed = (t: string) =>
  t.length >= MIN_TERM_LENGTH && t.length <= MAX_TERM_LENGTH && !ENCODER_STOPWORDS.has(t);

/**
 * The parts the keyword index splits an identifier into, as `SparseEncoder.Emit` does:
 * at lower-to-upper, at a change between digit and non-digit, and before the last capital
 * of an acronym that starts a word (`HTTPServer` is `HTTP`, `Server`).
 */
export function identifierParts(run: string): string[] {
  const upper = (c?: string) => c !== undefined && /\p{Lu}/u.test(c);
  const lower = (c?: string) => c !== undefined && /\p{Ll}/u.test(c);
  const digit = (c?: string) => c !== undefined && /\p{Nd}/u.test(c);

  const parts: string[] = [];
  let part = '';
  for (let i = 0; i < run.length; i++) {
    const c = run[i];
    const boundary = i > 0 && (
      (upper(c) && !upper(run[i - 1])) ||
      digit(c) !== digit(run[i - 1]) ||
      (upper(c) && lower(run[i + 1]) && upper(run[i - 1])));
    if (boundary && part) { parts.push(part); part = ''; }
    part += c;
  }
  if (part) parts.push(part);
  return parts;
}

/**
 * The terms a query marks: its plain words, and each compound whole, with its parts.
 *
 * A compound is joined by underscores (`not_found`) or split at identifier boundaries
 * (`NotFound`). The index reads both as separate runs, so their parts follow the index's
 * rule alone, and the prose exception applies only to a word standing on its own.
 */
export function queryTerms(query: string): Set<string> {
  const terms = new Set<string>();
  for (const token of query.match(/[\p{L}\p{Nd}_]+/gu) ?? []) {
    const parts = token.split('_').filter(Boolean).flatMap(identifierParts).map((p) => p.toLowerCase());
    if (parts.length === 0) continue;

    if (parts.length === 1) {
      if (indexed(parts[0]) && !PROSE_STOPWORDS.has(parts[0])) terms.add(parts[0]);
      continue;
    }

    // Whole as well, so a compound is marked in one piece where it appears whole.
    const whole = token.replace(/^_+|_+$/g, '').toLowerCase();
    if (indexed(whole)) terms.add(whole);
    for (const run of token.split('_').filter(Boolean)) {
      if (indexed(run.toLowerCase())) terms.add(run.toLowerCase());
    }
    for (const part of parts) if (indexed(part)) terms.add(part);
  }
  return terms;
}

/**
 * Split text into alternating non-match / match segments for the query's terms: matches at
 * the odd indices, which is what the caller relies on.
 *
 * The passage is walked token by token, and a token is marked whole when it is a term, or
 * else in the parts that are, so a term is marked where the index would match it and not
 * inside another word. The query's words with underscores are terms too, so `corpus_id` is
 * marked in one piece where it appears whole.
 *
 * It marked whole query words as substrings. That left a keyword search for an identifier
 * with nothing marked in nine passages of ten, each matched on a part; and once parts were
 * terms, "set" from `ChunkSet` was marked inside "settings", which the index does not match.
 */
export function splitOnTerms(text: string, query: string): string[] {
  const terms = queryTerms(query);
  if (terms.size === 0) return [text];

  const out: string[] = [];
  let plain = '';
  const mark = (s: string) => { out.push(plain, s); plain = ''; };

  for (const [token] of text.matchAll(/[\p{L}\p{Nd}_]+|[^\p{L}\p{Nd}_]+/gu)) {
    if (terms.has(token.toLowerCase())) { mark(token); continue; }
    if (!/^[\p{L}\p{Nd}_]/u.test(token)) { plain += token; continue; }
    // An underscore separates runs in the index, as any non-alphanumeric does.
    for (const piece of token.split(/(_+)/)) {
      if (piece === '' || piece.startsWith('_')) { plain += piece; continue; }
      if (terms.has(piece.toLowerCase())) { mark(piece); continue; }
      for (const part of identifierParts(piece)) {
        if (terms.has(part.toLowerCase())) mark(part);
        else plain += part;
      }
    }
  }
  out.push(plain);
  return out;
}
