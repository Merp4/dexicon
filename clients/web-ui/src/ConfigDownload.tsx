import { useState } from 'react';
import { Download } from 'lucide-react';
import { api } from './api';
import { Button, ErrorBanner, Notice, Spinner } from './ui';

/**
 * The configuration as a file: corpora with their filters, sources and chunk sets, the saved model profiles, and each
 * key's name, scopes and reach. It names everything by name and holds no key secret, no id and no indexed content.
 * What this instance holds that the file leaves out (the uploaded documents of a corpus, and any stored setting that could
 * not be read) is said under the button.
 */

/** The file's name: the day it was made, in the browser's calendar. */
export function fileName(now: Date): string {
  const two = (n: number) => String(n).padStart(2, '0');
  return `dexicon-config-${now.getFullYear()}-${two(now.getMonth() + 1)}-${two(now.getDate())}.json`;
}

/** The text of the download: the server's JSON, indented, ending in a line break so a diff of it is clean. */
export function fileText(file: unknown): string {
  return `${JSON.stringify(file, null, 2)}\n`;
}

export function ConfigDownload() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [omitted, setOmitted] = useState<string[]>([]);

  async function download() {
    setBusy(true);
    setError(null);
    try {
      const file = await api.exportConfig();
      const url = URL.createObjectURL(new Blob([fileText(file)], { type: 'application/json' }));
      const link = document.createElement('a');
      link.href = url;
      link.download = fileName(new Date());
      document.body.appendChild(link);
      link.click();
      link.remove();
      // After the click has been handled: some browsers cancel a download whose address is released at once.
      setTimeout(() => URL.revokeObjectURL(url), 0);
      setOmitted(file.omitted ?? []);
    } catch (e) {
      setError(e);
      setOmitted([]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="card p-3.5">
      <h2 className="mt-0 mx-0 mb-3 text-base">Configuration</h2>
      <p className="mt-0 mb-3 text-sm text-muted-foreground">
        Download the corpora, sources, chunk sets, model profiles and keys as one JSON file. Keys are in it by name, scopes
        and reach, without their secrets.
      </p>

      <Button disabled={busy} onClick={() => void download()}>
        {busy ? <Spinner /> : <Download />}
        {busy ? 'Preparing…' : 'Download configuration'}
      </Button>

      <ErrorBanner error={error} onDismiss={() => setError(null)} />

      {omitted.length > 0 && (
        <Notice tone="neutral" className="mt-3">
          <p className="m-0 font-medium">Left out of the file</p>
          <ul className="m-0 mt-1 list-disc pl-5">
            {omitted.map((line, i) => <li key={`${i}-${line}`}>{line}</li>)}
          </ul>
        </Notice>
      )}
    </section>
  );
}
