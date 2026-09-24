import ts from 'typescript';
import { describe, expect, it } from 'vitest';

/**
 * Every dialog shows its own failures.
 *
 * The page's error banner sits under an open dialog's overlay, and Radix marks everything
 * outside the dialog aria-hidden. Eleven dialogs handed their failures to the page, so a
 * refused save left the dialog open with nothing in it to say why: measured with New
 * corpus and a name already taken.
 *
 * Read from the source rather than rendered, because the defect is a call made in the
 * wrong place, and a render test per dialog would only cover the dialogs someone thought
 * to write one for.
 */

// Every component source, so a dialog added in a new file is read too.
const files = import.meta.glob<string>(['./**/*.tsx', '!./**/*.test.tsx'], {
  query: '?raw',
  import: 'default',
  eager: true,
});

/**
 * Pages that render a Modal inline, and are not dialogs. Named rather than inferred from a
 * naming convention, so a dialog called anything at all is still found.
 */
const pages: Record<string, string> = {
  './App.tsx: AccessView':
    'The keys page. Its inline modal only displays a key just created and makes no request; '
    + 'its own actions report to the page banner, which is visible when no dialog is open.',
};

/**
 * Dialogs that do not show a failure themselves, and why that is right for each. A dialog
 * here must make no request of its own, which the test checks, so the reason cannot
 * quietly stop being true.
 */
const exempt: Record<string, string> = {
  './ChunkSets.tsx: ConfirmModal':
    'Asks and closes. Its callers close it before they act and report the failure in their '
    + 'own banner, which is visible once it has closed.',
};

/**
 * Every top-level declaration that renders a Modal, by file and name.
 *
 * Parsed rather than matched with a pattern: a dialog can be a function declaration, an
 * arrow function in a const, an `export default` or a wrapped component, and a pattern for
 * one of those forms passes a dialog written in another without checking it at all.
 */
function rendering(): { id: string; node: ts.Statement }[] {
  const found: { id: string; node: ts.Statement }[] = [];
  for (const [file, text] of Object.entries(files)) {
    const source = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
    for (const statement of source.statements) {
      if (renders(statement, 'Modal')) found.push({ id: `${file}: ${nameOf(statement)}`, node: statement });
    }
  }
  return found;
}

// The checks below read the syntax tree, not the text, so a comment or a string that
// happens to spell "<ErrorBanner" or "onError" neither satisfies nor trips them.

function some(node: ts.Node, test: (n: ts.Node) => boolean): boolean {
  return test(node) || (ts.forEachChild(node, (child) => some(child, test) || undefined) ?? false);
}

function renders(node: ts.Node, tag: string): boolean {
  return some(node, (n) => (ts.isJsxOpeningElement(n) || ts.isJsxSelfClosingElement(n)) && n.tagName.getText() === tag);
}

function mentions(node: ts.Node, identifier: string): boolean {
  return some(node, (n) => ts.isIdentifier(n) && n.text === identifier);
}

/** Any `api.something`: the client every request goes through. */
function callsTheApi(node: ts.Node): boolean {
  return some(node, (n) => ts.isPropertyAccessExpression(n) && ts.isIdentifier(n.expression) && n.expression.text === 'api');
}

function nameOf(statement: ts.Statement): string {
  if ((ts.isFunctionDeclaration(statement) || ts.isClassDeclaration(statement)) && statement.name) {
    return statement.name.text;
  }
  if (ts.isVariableStatement(statement)) {
    return statement.declarationList.declarations.map((d) => d.name.getText()).join(', ');
  }
  return 'default';
}

describe('dialogs', () => {
  const found = rendering();
  const all = found.filter((d) => !(d.id in pages));

  it('were found, so a clean result means something', () => {
    // App.tsx alone would pass a count; the glob has to have reached the other files.
    expect(Object.keys(files)).toEqual(expect.arrayContaining(['./App.tsx', './ChunkSets.tsx', './Documents.tsx']));
    // Sixteen dialogs and one page as this is written.
    expect(all.length).toBeGreaterThanOrEqual(16);
    expect(Object.keys(pages).filter((id) => !found.some((d) => d.id === id))).toEqual([]);
  });

  it('do not take the page error handler at all', () => {
    // Any mention, not only a call: `onError?.(e)`, `catch( onError )` and passing it on
    // are the same defect, and no dialog has a reason to hold it.
    expect(all.filter((d) => mentions(d.node, 'onError')).map((d) => d.id)).toEqual([]);
  });

  it('show them in a banner of their own, unless exempt for a stated reason', () => {
    const missing = all
      .filter((d) => !renders(d.node, 'ErrorBanner') && !(d.id in exempt))
      .map((d) => d.id);
    expect(missing).toEqual([]);
  });

  it('are exempt only while they make no request of their own', () => {
    const stale = all.filter((d) => d.id in exempt && callsTheApi(d.node)).map((d) => d.id);
    expect(stale).toEqual([]);

    // And every exemption names a dialog that exists.
    expect(Object.keys(exempt).filter((id) => !all.some((d) => d.id === id))).toEqual([]);
  });
});
