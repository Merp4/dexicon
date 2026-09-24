import { describe, expect, it } from 'vitest';
import appSource from './App.tsx?raw';
import chunkSetsSource from './ChunkSets.tsx?raw';
import documentsSource from './Documents.tsx?raw';

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
const files = { 'App.tsx': appSource, 'ChunkSets.tsx': chunkSetsSource, 'Documents.tsx': documentsSource };

function dialogs(): { file: string; name: string; body: string }[] {
  const found: { file: string; name: string; body: string }[] = [];
  for (const [file, text] of Object.entries(files)) {
    const starts = [...text.matchAll(/^(?:export )?function (\w+)\(/gm)];
    // A dialog component by its name. "Renders a Modal" also takes in AccessView, a page
    // whose own actions report to the page and whose inline modal only displays a key.
    starts.forEach((m, i) => {
      const body = text.slice(m.index, starts[i + 1]?.index ?? text.length);
      if (/(Modal|Viewer)$/.test(m[1]) && /<Modal\b/.test(body)) found.push({ file, name: m[1], body });
    });
  }
  return found;
}

describe('dialogs', () => {
  it('handle their own failures rather than sending them to the page', () => {
    const all = dialogs();

    // The scan has to have found the dialogs, or a clean result means nothing. Sixteen
    // as this is written.
    expect(all.length).toBeGreaterThanOrEqual(16);

    const offenders = all
      .filter((d) => /\bonError\(|catch\(onError\)/.test(d.body))
      .map((d) => `${d.file}: ${d.name}`);
    expect(offenders).toEqual([]);
  });
});
