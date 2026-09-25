import { useEffect, useState } from 'react';
import { api, type GitRef, type GitRefsResponse } from './api';
import {
  Button, Field, Input, Notice, Segmented, Select, SelectGroup, SelectItem, SelectLabel, relativeTime,
} from './ui';
import { distance, isStale, listedRef, refLabel } from './lib/refs';

type Mode = 'head' | 'branch' | 'other';

/**
 * What a history source follows: the branch the checkout has out, a branch picked from the
 * repository, or a ref typed in, for a tag or a commit.
 *
 * It was a text box alone, and nothing on screen said a local branch was behind its
 * upstream: dexhistory sat on one 52 commits behind origin/main for three days with every
 * count correct. So the picker lists the repository's own refs, says how far each local
 * branch is from its upstream as of the last fetch, and says when that was. Dexicon does
 * not fetch; the host does, with its own login, and this reads what it left.
 *
 * Stores the same `ref` string the text box did. A pick stores the full name; a source
 * saved with a short name shows as picked and is not rewritten until someone picks again.
 */
export function GitRefPicker({
  value,
  onChange,
  repositoryPath,
}: {
  value: string;
  onChange: (ref: string) => void;
  /** The folder the source reads, relative to the workspace; '' is the root. Null before one is chosen. */
  repositoryPath: string | null;
}) {
  const [read, setRead] = useState<{ path: string; result?: GitRefsResponse; error?: unknown } | null>(null);

  // Chosen by the person, or null to follow what the value says. Derived rather than set
  // when the listing arrives, so a stored ref shows in the mode it belongs to.
  const [chosen, setChosen] = useState<Mode | null>(null);

  useEffect(() => {
    setChosen(null);
    if (repositoryPath === null) {
      setRead(null);
      return;
    }

    let current = true;
    setRead({ path: repositoryPath });
    api.repositoryRefs(repositoryPath).then(
      (result) => { if (current) setRead({ path: repositoryPath, result }); },
      (error: unknown) => { if (current) setRead({ path: repositoryPath, error }); },
    );
    return () => { current = false; };
  }, [repositoryPath]);

  const listing = read?.result?.isRepository ? read.result.listing : null;
  const picked = listing ? listedRef(listing, value) : null;
  const mode: Mode = chosen ?? (value === 'HEAD' ? 'head' : picked ? 'branch' : 'other');

  const headRef = listing?.head.branch
    ? listing.local.refs.find((r) => r.name === listing.head.branch)
    : undefined;

  const choose = (next: Mode) => {
    setChosen(next);
    if (next === 'head') onChange('HEAD');
    if (next === 'branch' && listing && !picked) {
      const first = headRef ?? listing.local.refs.find((r) => !r.refusal);
      if (first) onChange(first.name);
    }
  };

  const loading = repositoryPath !== null && !read?.result && read?.error === undefined;

  return (
    <div className="mb-3.5 grid gap-2">
      <Segmented
        label="Follow"
        value={mode}
        onChange={choose}
        options={[
          { value: 'head', label: 'Checked-out branch', title: 'HEAD: whatever branch the checkout has out' },
          ...(listing ? [{ value: 'branch' as const, label: 'A branch', title: 'A local, remote-tracking or prefetched branch' }] : []),
          { value: 'other', label: 'Other ref', title: 'A tag, a commit, or any ref git can name' },
        ]}
      />

      {repositoryPath === null && (
        <p className="dim m-0 text-xs">Choose a folder to list its branches.</p>
      )}
      {loading && <p className="dim m-0 text-xs">Reading the repository's branches…</p>}
      {read?.error !== undefined && (
        <Notice tone="warn" className="text-xs">
          The branches could not be listed: {read.error instanceof Error ? read.error.message : String(read.error)} A
          ref can still be typed.
        </Notice>
      )}
      {read?.result && !read.result.isRepository && (
        <Notice tone="warn" className="text-xs">
          This folder is not a repository's root, so it has no history of its own. Choose the folder that holds
          .git.
        </Notice>
      )}

      {mode === 'head' && listing && (
        <>
          <p className="m-0 text-xs">
            {listing.head.branch ? (
              <>
                Follows <span className="mono">{refLabel(listing.head.branch)}</span>, the branch the checkout has
                out{headRef?.upstream && distance(headRef.upstream) ? `, ${distance(headRef.upstream)}` : ''}
                {headRef?.upstream && ` as of the last fetch${listing.lastFetchUtc ? ` ${relativeTime(listing.lastFetchUtc)}` : ''}`}.
              </>
            ) : listing.head.sha ? (
              <>
                The checkout is detached at <span className="mono">{listing.head.sha.slice(0, 7)}</span>: the source
                follows that commit until the checkout moves.
              </>
            ) : (
              'The repository has no commits yet.'
            )}
          </p>
          {headRef?.upstream && isStale(headRef.upstream) && !headRef.upstream.gone && (
            <Notice tone="warn" className="flex flex-wrap items-center gap-2 text-xs">
              <span>A local branch moves only when someone pulls.</span>
              {/* type="button": this sits inside the add dialog's form, and a button there
                  submits by default, which added the source before the ref changed. */}
              <Button
                type="button"
                size="xs"
                onClick={() => {
                  setChosen('branch');
                  onChange(headRef.upstream!.name);
                }}
              >
                Follow {headRef.upstream.shortName} instead
              </Button>
            </Notice>
          )}
          <p className="dim m-0 text-xs">
            Switching the checkout changes what is indexed: commits the new branch does not reach leave on the next
            refresh.
          </p>
        </>
      )}

      {mode === 'branch' && listing && (
        <>
          <Field label="Branch" hint="Stored by its full name, so a tag of the same name cannot take its place.">
            <Select value={picked?.ref.name ?? ''} onValueChange={onChange} placeholder="Choose a branch">
              <SelectGroup>
                <SelectLabel>Local{listing.local.truncated ? ', newest 200' : ''}</SelectLabel>
                {listing.local.refs.map((r) => (
                  <RefItem key={r.name} r={r}>
                    {r.upstream ? distance(r.upstream) ?? r.upstream.shortName : 'no upstream'}
                  </RefItem>
                ))}
              </SelectGroup>
              {listing.remoteTracking.refs.length > 0 && (
                <SelectGroup>
                  <SelectLabel>
                    Remote-tracking{listing.remoteTracking.truncated ? ', newest 200' : ''}
                    {listing.lastFetchUtc ? `, last git fetch ${relativeTime(listing.lastFetchUtc)}` : ', never fetched'}
                  </SelectLabel>
                  {listing.remoteTracking.refs.map((r) => (
                    <RefItem key={r.name} r={r}>{r.committedUtc ? `last commit ${relativeTime(r.committedUtc)}` : null}</RefItem>
                  ))}
                </SelectGroup>
              )}
              {listing.prefetched.refs.length > 0 && (
                <SelectGroup>
                  <SelectLabel>Prefetched by git maintenance{listing.prefetched.truncated ? ', newest 200' : ''}</SelectLabel>
                  {listing.prefetched.refs.map((r) => (
                    <RefItem key={r.name} r={r}>
                      {r.committedUtc ? `last commit ${relativeTime(r.committedUtc)}` : null}
                      {r.mirrors && r.sameAsMirrored != null
                        ? `, ${r.sameAsMirrored ? 'same as' : 'ahead of'} ${refLabel(r.mirrors)}`
                        : null}
                    </RefItem>
                  ))}
                </SelectGroup>
              )}
            </Select>
          </Field>

          {picked?.kind === 'local' && picked.ref.upstream && isStale(picked.ref.upstream) && (
            <Notice tone="warn" className="flex flex-wrap items-center gap-2 text-xs">
              <span>
                {refLabel(picked.ref.name)} is {distance(picked.ref.upstream)} as of the last fetch. A local branch
                moves only when someone pulls.
              </span>
              {!picked.ref.upstream.gone && (
                <Button type="button" size="xs" onClick={() => onChange(picked.ref.upstream!.name)}>
                  Follow {picked.ref.upstream.shortName} instead
                </Button>
              )}
            </Notice>
          )}
          {picked?.kind === 'remote' && (
            <p className="dim m-0 text-xs">
              Moves when the host runs git fetch. Dexicon does not fetch.
              {listing.lastFetchUtc ? ` The last fetch was ${relativeTime(listing.lastFetchUtc)}.` : ' This repository has never been fetched.'}
            </p>
          )}
          {picked?.kind === 'prefetched' && (
            <p className="dim m-0 text-xs">
              Moves when git maintenance prefetches, hourly once <span className="mono">git maintenance start</span> has
              been run in this repository on the host. git records no time for a prefetch, so its last commit is the only
              date there is.
            </p>
          )}
        </>
      )}

      {mode === 'other' && (
        <Field label="Ref" hint="A branch, a tag or a commit, as git names it. One that names nothing shows on the corpus after the refresh.">
          <Input className="mono" value={value} onChange={(e) => onChange(e.target.value)} placeholder="HEAD" />
        </Field>
      )}
    </div>
  );
}

/**
 * One ref in the picker. Short on purpose: an item's text is also what the closed select
 * shows. A name the server refuses is listed, disabled, with the reason on hover, so a
 * branch that exists is never simply missing.
 */
function RefItem({ r, children }: { r: GitRef; children: React.ReactNode }) {
  return (
    <SelectItem value={r.name} disabled={!!r.refusal} title={r.refusal ?? undefined}>
      <bdi className="mono">{refLabel(r.name)}</bdi>
      {/* The separator is a text node of its own. Inside the span, its leading space was
          at an element's edge, which an accessible name drops: "main· 52 behind". */}
      {(r.refusal || children) && ' · '}
      <span className="dim text-xs">{r.refusal ? 'cannot be followed' : children}</span>
    </SelectItem>
  );
}
