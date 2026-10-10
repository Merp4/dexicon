/**
 * A list of globs typed into a form, separated by commas or newlines, with the blanks dropped.
 *
 * A comma inside the parentheses of pathspec magic belongs to the element, so `:(glob,icase)docs/*.md` is
 * one pattern. The parentheses group only where an element begins with `:(` (leading blanks aside), so a name
 * such as `a(b,c)` splits at its comma, and they group up to the first `)`, as git reads the magic. A newline
 * always ends an element, so an unclosed `:(` does not take the lines after it. An element with a comma that is
 * not in magic cannot be written in this field.
 *
 * One pass over the text, with no work on an element that grows with its length: the form calls this on every
 * render.
 */
export function globList(raw: string): string[] {
  const globs: string[] = [];
  let begin = 0;
  // Where the element's first character that is not a blank is, or -1 before there is one.
  let content = -1;
  let inMagic = false;

  for (let i = 0; i < raw.length; i++) {
    const c = raw[i];

    if (c === '\n' || (c === ',' && !inMagic)) {
      globs.push(raw.slice(begin, i));
      begin = i + 1;
      content = -1;
      inMagic = false;
      continue;
    }

    if (content < 0 && c.trim() !== '') content = i;

    if (inMagic) {
      if (c === ')') inMagic = false;
    } else if (c === '(' && content === i - 1 && raw[content] === ':') {
      inMagic = true;
    }
  }

  globs.push(raw.slice(begin));
  return globs.map((g) => g.trim()).filter(Boolean);
}
/**
 * An include list as the server gives it to git, which is how the content fingerprint of a history source
 * sees it. Mirrors GitHistory.Pathspecs: one leading `/` is removed unless `//` or `/:` follows, and an element
 * that is only `/` is dropped. So `docs` and `/docs` are one filter and editing one into the other re-reads
 * nothing. GitHistoryTests holds the same cases.
 */
export function gitPathspecs(globs: readonly string[]): string[] {
  const specs: string[] = [];

  for (const glob of globs) {
    if (glob === '/') continue;

    const rooted = glob.startsWith('/') && !glob.startsWith('//') && !glob.startsWith('/:');
    specs.push(rooted ? glob.slice(1) : glob);
  }

  return specs;
}
