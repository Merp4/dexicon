/**
 * A list of globs typed into a form, separated by commas or newlines, with the blanks dropped.
 *
 * A comma inside the parentheses of pathspec magic belongs to the element: `:(glob,icase)docs/*.md` is one
 * pattern, and splitting it at the comma stored `:(glob` and `icase)docs/*.md`, which git rejects. The
 * parentheses group only where an element begins with `:(`, so a name such as `a(b,c)` still splits at its
 * comma, and they group up to the first `)`, as git reads the magic.
 */
export function globList(raw: string): string[] {
  const globs: string[] = [];
  let current = '';
  let inMagic = false;

  for (let i = 0; i < raw.length; i++) {
    const c = raw[i];

    if (inMagic) {
      current += c;
      if (c === ')') inMagic = false;
    } else if (c === ',' || c === '\n') {
      globs.push(current);
      current = '';
    } else {
      current += c;
      if (c === '(' && current.trim() === ':(') inMagic = true;
    }
  }

  globs.push(current);
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
