import { useEffect, useState } from 'react';
import { ChevronRight, CornerLeftUp, Folder } from 'lucide-react';
import { api } from './api';
import { Spinner } from './ui';

/**
 * Choosing a folder under the mounted workspace root — at any depth.
 *
 * `GET /api/workspaces` has always taken a `path` and returned the directories beneath
 * it, and `api.browse` has always had the parameter. Neither caller ever passed one, so
 * the two forms that point a corpus at a folder were flat lists of the top level: with
 * WORKSPACE_ROOT holding a shelf of books at books/orly/Architecture, the only reachable
 * choice was `books` — all 1.5 GB of it. Anything organised into subfolders, which is
 * what a workspace root normally is, could not be addressed from the UI at all.
 *
 * The current directory IS the selection. Descending into a folder selects it, so the
 * thing on screen and the thing that will be indexed are never different — there is no
 * separate "use this one" to forget to press.
 */
export function WorkspacePicker({
  value,
  onChange,
  emptyLabel,
  disabled,
}: {
  /** Selected path, relative to the workspace root. '' is the root itself. */
  value: string;
  onChange: (path: string) => void;
  /** What selecting the root means in this form — it differs between creating and adding. */
  emptyLabel: string;
  disabled?: boolean;
}) {
  const [entries, setEntries] = useState<{ name: string; relativePath: string; childCount?: number | null }[]>([]);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState<string | null>(null);

  useEffect(() => {
    let current = true;
    setLoading(true);
    setFailed(null);

    api.browse(value || undefined)
      .then((l) => {
        if (!current) return;
        setEntries(l.entries.filter((e) => e.isDirectory));
      })
      .catch((e: unknown) => {
        if (!current) return;
        // A path that has gone away should say so rather than render as an empty folder,
        // which is indistinguishable from a folder with nothing in it.
        setEntries([]);
        setFailed(e instanceof Error ? e.message : 'Could not read that folder.');
      })
      .finally(() => current && setLoading(false));

    return () => {
      current = false;
    };
  }, [value]);

  const segments = value ? value.split('/') : [];
  const parent = segments.slice(0, -1).join('');

  return (
    <div className="rounded-md border border-[var(--border)] bg-[var(--surface)]">
      <div className="flex flex-wrap items-center gap-0.5 border-b border-[var(--border)] px-2 py-1.5 text-xs">
        <button
          type="button"
          disabled={disabled}
          onClick={() => onChange('')}
          className="rounded px-1.5 py-0.5 hover:bg-[var(--surface-2)] disabled:opacity-50"
        >
          {segments.length === 0 ? emptyLabel : 'workspace root'}
        </button>

        {segments.map((seg, i) => (
          <span key={i} className="flex items-center gap-0.5">
            <ChevronRight className="size-3 opacity-40" aria-hidden />
            <button
              type="button"
              disabled={disabled}
              onClick={() => onChange(segments.slice(0, i + 1).join('/'))}
              className="rounded px-1.5 py-0.5 hover:bg-[var(--surface-2)] disabled:opacity-50"
            >
              {seg}
            </button>
          </span>
        ))}
      </div>

      <div className="max-h-44 overflow-y-auto p-1">
        {loading ? (
          <div className="flex items-center gap-2 px-2 py-3 text-xs opacity-70">
            <Spinner className="size-3" /> Reading…
          </div>
        ) : failed ? (
          <p className="px-2 py-3 text-xs text-[var(--danger)]">{failed}</p>
        ) : (
          <>
            {segments.length > 0 && (
              <button
                type="button"
                disabled={disabled}
                onClick={() => onChange(segments.slice(0, -1).join('/'))}
                className="flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-xs hover:bg-[var(--surface-2)] disabled:opacity-50"
              >
                <CornerLeftUp className="size-3.5 opacity-60" aria-hidden />
                <span className="opacity-70">{parent === '' ? 'workspace root' : parent}</span>
              </button>
            )}

            {entries.map((e) => (
              <button
                key={e.relativePath}
                type="button"
                disabled={disabled}
                onClick={() => onChange(e.relativePath)}
                className="flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-xs hover:bg-[var(--surface-2)] disabled:opacity-50"
              >
                <Folder className="size-3.5 opacity-60" aria-hidden />
                <span className="truncate">{e.name}</span>
                {e.childCount != null && (
                  <span className="ml-auto shrink-0 tabular-nums opacity-50">
                    {e.childCount >= 500 ? '500+' : e.childCount}
                  </span>
                )}
              </button>
            ))}

            {entries.length === 0 && (
              <p className="px-2 py-3 text-xs opacity-60">
                {segments.length === 0
                  ? 'Nothing is mounted. Set WORKSPACE_ROOT to a folder with something in it.'
                  : 'No subfolders; this one indexes on its own.'}
              </p>
            )}
          </>
        )}
      </div>

      <p className="border-t border-[var(--border)] px-2 py-1.5 text-xs">
        {value ? (
          <>
            Indexing <code className="rounded bg-[var(--surface-2)] px-1 py-0.5">{value}</code> and everything beneath it.
          </>
        ) : (
          <span className="opacity-70">{emptyLabel}</span>
        )}
      </p>
    </div>
  );
}
