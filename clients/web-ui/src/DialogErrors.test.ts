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

/**
 * Dialogs that do not show a failure themselves, and why that is right for each. A dialog
 * here must make no request of its own, which the test checks, so the reason cannot
 * quietly stop being true.
 */
const exempt: Record<string, string> = {
  'ChunkSets.tsx: ConfirmModal':
    'Asks and closes. Its callers close it before they act and report the failure in their '
    + 'own banner, which is visible once it has closed.',
};

function dialogs(): { id: string; body: string }[] {
  const found: { id: string; body: string }[] = [];
  for (const [file, text] of Object.entries(files)) {
    const starts = [...text.matchAll(/^(?:export )?function (\w+)\(/gm)];
    // A dialog component by its name. "Renders a Modal" also takes in AccessView, a page
    // whose own actions report to the page and whose inline modal only displays a key.
    starts.forEach((m, i) => {
      const body = text.slice(m.index, starts[i + 1]?.index ?? text.length);
      if (/(Modal|Viewer)$/.test(m[1]) && /<Modal\b/.test(body)) found.push({ id: `${file}: ${m[1]}`, body });
    });
  }
  return found;
}

describe('dialogs', () => {
  const all = dialogs();

  it('were found, so a clean result means something', () => {
    // Sixteen as this is written.
    expect(all.length).toBeGreaterThanOrEqual(16);
  });

  it('do not send their failures to the page', () => {
    expect(all.filter((d) => /\bonError\(|catch\(onError\)/.test(d.body)).map((d) => d.id)).toEqual([]);
  });

  it('show them in a banner of their own, unless exempt for a stated reason', () => {
    const missing = all
      .filter((d) => !/<ErrorBanner\b/.test(d.body) && !(d.id in exempt))
      .map((d) => d.id);
    expect(missing).toEqual([]);
  });

  it('are exempt only while they make no request of their own', () => {
    const stale = all.filter((d) => d.id in exempt && /\bapi\./.test(d.body)).map((d) => d.id);
    expect(stale).toEqual([]);

    // And every exemption names a dialog that exists.
    expect(Object.keys(exempt).filter((id) => !all.some((d) => d.id === id))).toEqual([]);
  });
});
