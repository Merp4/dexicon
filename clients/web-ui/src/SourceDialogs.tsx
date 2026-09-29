import { useState } from 'react';
import { Check, Plus } from 'lucide-react';
import { api, type Corpus } from './api';
import {
  Button, CheckField, Checkbox, ErrorBanner, Field, Input, Modal, Notice, Section, Segmented, Spinner, formatBytes,
} from './ui';
import { sourceName } from './lib/sources';
import { WorkspacePicker } from './WorkspacePicker';
import { GitRefPicker } from './GitRefPicker';

/**
 * Adding a source, editing one, and the corpus defaults they follow.
 *
 * One set of fields for all four dialogs, so that a filter reads the same wherever it is
 * set. They had drifted: the add dialog had a checkbox for commit history halfway down a
 * form of file settings, labelled its globs "Only these" and "Never these", and pinned a
 * size cap and a .gitignore setting on every source it added, so a corpus default for
 * either reached none of them.
 */

type Source = Corpus['sources'][number];
type Kind = 'workspace' | 'githistory';

const MB = 1024 * 1024;

/** A comma or newline separated list, with the blanks dropped. */
function globList(raw: string): string[] {
  return raw.split(/[\n,]/).map((g) => g.trim()).filter(Boolean);
}

// ── Defaults ────────────────────────────────────────────────────────────────

/** Where a value a source does not set comes from, and what it is. */
type Fallback<T> = { value: T; from: 'corpus' | 'server' };

type Fallbacks = {
  useGitignore: Fallback<boolean>;
  /** Null when the server did not say, which an older server does not. */
  maxFileBytes: Fallback<number | null>;
  /** Null where there is no layer above to follow: the server sets no globs. */
  includeGlobs: Fallback<string[]> | null;
  excludeGlobs: Fallback<string[]> | null;
};

/**
 * What following the default gives each filter of a source in this corpus: the corpus's
 * value where it sets one, and the server's where it does not.
 *
 * The edit dialog showed the source's own value as the corpus default, so a source pinned
 * at 64 MB offered "corpus default (64.0 MB)" when following it would have given 256 KB.
 */
function sourceFallbacks(corpus: Corpus): Fallbacks {
  const d = corpus.defaults;
  const server = serverFallbacks(corpus);
  return {
    useGitignore: d?.useGitignore != null ? { value: d.useGitignore, from: 'corpus' } : server.useGitignore,
    maxFileBytes: d?.maxFileBytes != null ? { value: d.maxFileBytes, from: 'corpus' } : server.maxFileBytes,
    includeGlobs: { value: d?.includeGlobs ?? [], from: 'corpus' },
    excludeGlobs: { value: d?.excludeGlobs ?? [], from: 'corpus' },
  };
}

/**
 * The corpus's own layer follows the server's. `.gitignore` is on there unless configured
 * otherwise, which nothing configures; an older server that does not send `configured` has
 * the same rule.
 */
function serverFallbacks(corpus: Corpus): Fallbacks {
  const c = corpus.configured;
  return {
    useGitignore: { value: c?.useGitignore ?? true, from: 'server' },
    maxFileBytes: { value: c?.maxFileBytes ?? null, from: 'server' },
    includeGlobs: null,
    excludeGlobs: null,
  };
}

/**
 * The tick that says a field follows the layer above. It names that layer, because the
 * value beside it is only right while the reader knows where it came from.
 */
function UseDefault({ field, from, checked, onChange }: {
  field: string;
  from: 'corpus' | 'server';
  checked: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <label className="flex shrink-0 items-center gap-1.5 text-xs text-muted-foreground">
      <Checkbox
        checked={checked}
        onCheckedChange={(v) => onChange(v === true)}
        aria-label={`${field}: use the ${from} default`}
      />
      {from === 'corpus' ? 'Corpus default' : 'Server default'}
    </label>
  );
}

// ── File filters ────────────────────────────────────────────────────────────

/**
 * A file source's four filters, each either following the default or set. The values are
 * kept while a field follows, so ticking and unticking does not lose what was typed.
 */
type FileFilters = {
  followInclude: boolean;
  followExclude: boolean;
  followGitignore: boolean;
  followCap: boolean;
  include: string;
  exclude: string;
  useGitignore: boolean;
  maxFileMb: number;
};

/**
 * A source's filters as the form starts them: what it sets, and where it follows, the
 * value following gives, so unticking a default does not blank the field.
 */
function fileFiltersOf(source: Source | null, fallback: Fallbacks): FileFilters {
  return {
    followInclude: source?.ownIncludeGlobs == null,
    followExclude: source?.ownExcludeGlobs == null,
    followGitignore: source?.ownUseGitignore == null,
    followCap: source?.ownMaxFileBytes == null,
    include: (source?.ownIncludeGlobs ?? fallback.includeGlobs?.value ?? []).join(', '),
    exclude: (source?.ownExcludeGlobs ?? fallback.excludeGlobs?.value ?? []).join(', '),
    useGitignore: source?.ownUseGitignore ?? fallback.useGitignore.value,
    maxFileMb: (source?.ownMaxFileBytes ?? fallback.maxFileBytes.value ?? 0) / MB,
  };
}

/** The size limit is usable: zero would index nothing. */
const capUsable = (f: FileFilters) => f.followCap || f.maxFileMb > 0;

/**
 * The filters as a request sends them. Undefined for a field that follows, so an add
 * leaves it null on the source, and an edit names it in `clear`.
 */
function fileRequest(f: FileFilters) {
  return {
    includeGlobs: f.followInclude ? undefined : globList(f.include),
    excludeGlobs: f.followExclude ? undefined : globList(f.exclude),
    useGitignore: f.followGitignore ? undefined : f.useGitignore,
    maxFileBytes: f.followCap ? undefined : Math.round(f.maxFileMb * MB),
  };
}

function FileFilterFields({ value, onChange, fallback, documentMaxBytes }: {
  value: FileFilters;
  onChange: (next: FileFilters) => void;
  fallback: Fallbacks;
  documentMaxBytes: number | null;
}) {
  const set = <K extends keyof FileFilters>(key: K, v: FileFilters[K]) => onChange({ ...value, [key]: v });
  const { includeGlobs, excludeGlobs, useGitignore, maxFileBytes } = fallback;

  // Following, a field shows the value it follows, disabled, so the answer to "what will
  // this do" is on screen either way.
  const include = value.followInclude && includeGlobs ? includeGlobs.value.join(', ') : value.include;
  const exclude = value.followExclude && excludeGlobs ? excludeGlobs.value.join(', ') : value.exclude;
  const followedCap = maxFileBytes.value == null ? '' : maxFileBytes.value / MB;

  return (
    <Section title="Which files">
      <Field
        label="Include"
        hint="Glob patterns, comma separated. Empty includes every file."
        aside={includeGlobs && (
          <UseDefault field="Include" from={includeGlobs.from} checked={value.followInclude}
            onChange={(v) => set('followInclude', v)} />
        )}
      >
        <Input
          className="mono"
          value={include}
          disabled={value.followInclude && !!includeGlobs}
          onChange={(e) => set('include', e.target.value)}
          placeholder={value.followInclude && includeGlobs ? 'every file' : 'src/**, docs/**'}
        />
      </Field>

      <Field
        label="Exclude"
        hint="Glob patterns, comma separated, applied after Include."
        aside={excludeGlobs && (
          <UseDefault field="Exclude" from={excludeGlobs.from} checked={value.followExclude}
            onChange={(v) => set('followExclude', v)} />
        )}
      >
        <Input
          className="mono"
          value={exclude}
          disabled={value.followExclude && !!excludeGlobs}
          onChange={(e) => set('exclude', e.target.value)}
          placeholder={value.followExclude && excludeGlobs ? 'nothing' : '**/vendor/**, *.min.js'}
        />
      </Field>

      <CheckField
        label="Respect .gitignore and .dexiconignore"
        hint="Off, build output and dependencies are indexed too."
        checked={value.followGitignore ? useGitignore.value : value.useGitignore}
        disabled={value.followGitignore}
        onChange={(v) => set('useGitignore', v)}
        aside={
          <UseDefault field=".gitignore" from={useGitignore.from} checked={value.followGitignore}
            onChange={(v) => set('followGitignore', v)} />
        }
      />

      <Field
        label="Size limit (MB)"
        hint={
          <>
            For code and text files. Larger ones are skipped and listed as skipped. PDF, DOCX, PPTX,
            EPUB and HTML files have their own limit{documentMaxBytes ? `, ${formatBytes(documentMaxBytes)}` : ''},
            set by the server.
          </>
        }
        aside={
          <UseDefault field="Size limit" from={maxFileBytes.from} checked={value.followCap}
            onChange={(v) => set('followCap', v)} />
        }
      >
        {/* No step: a limit is a free value, and a number off the input's grid is invalid,
            which makes the browser refuse to submit the form without saying why. 256 KB
            is 0.25 MB. */}
        <Input
          type="number"
          min={0.01}
          step="any"
          value={value.followCap ? followedCap : value.maxFileMb}
          disabled={value.followCap}
          placeholder={value.followCap ? "the server's setting" : undefined}
          onChange={(e) => set('maxFileMb', Number(e.target.value))}
        />
      </Field>
      {!capUsable(value) && (
        <Notice tone="danger" role="alert" className="-mt-2 mb-3.5 text-xs">
          A limit of zero indexes nothing. Tick the default beside it to follow that instead.
        </Notice>
      )}
    </Section>
  );
}

// ── History settings ────────────────────────────────────────────────────────

/** A history source's settings with every field present, since a save sends them all. */
type GitSettings = {
  ref: string;
  includeMessage: boolean;
  includeStat: boolean;
  includeDiff: boolean;
  maxDiffBytes: number;
  includeMerges: boolean;
  maxCommits: number | null;
  keepIndexed: boolean;
  since: string | null;
};

/** The server's defaults, spelled out so a form can show them. docs/04 lists them. */
const DEFAULT_GIT: GitSettings = {
  ref: 'HEAD',
  includeMessage: true,
  includeStat: true,
  includeDiff: false,
  maxDiffBytes: 65536,
  includeMerges: false,
  maxCommits: null,
  keepIndexed: false,
  since: null,
};

function gitSettingsOf(git?: Source['git']): GitSettings {
  return {
    ref: git?.ref ?? DEFAULT_GIT.ref,
    includeMessage: git?.includeMessage ?? DEFAULT_GIT.includeMessage,
    includeStat: git?.includeStat ?? DEFAULT_GIT.includeStat,
    includeDiff: git?.includeDiff ?? DEFAULT_GIT.includeDiff,
    maxDiffBytes: git?.maxDiffBytes ?? DEFAULT_GIT.maxDiffBytes,
    includeMerges: git?.includeMerges ?? DEFAULT_GIT.includeMerges,
    maxCommits: git?.maxCommits ?? null,
    keepIndexed: git?.keepIndexed ?? DEFAULT_GIT.keepIndexed,
    since: git?.since ?? null,
  };
}

/**
 * The paths a history source is limited to. They are the include globs a file source has,
 * used as pathspecs, so they follow the corpus's include default the same way.
 */
type HistoryPaths = { followPaths: boolean; paths: string };

function historyPathsOf(source: Source | null, fallback: Fallbacks): HistoryPaths {
  return {
    followPaths: source?.ownIncludeGlobs == null,
    paths: (source?.ownIncludeGlobs ?? fallback.includeGlobs?.value ?? []).join(', '),
  };
}

/**
 * What a history source reads from git, shared by adding one and editing one so the two
 * cannot drift apart. Split by what each setting changes: which commits become documents,
 * and what each document holds. The two cost different amounts to change, and the edit
 * dialog says which a save is.
 */
function HistoryFields({ git, onGitChange, paths, onPathsChange, fallback, repositoryPath }: {
  git: GitSettings;
  onGitChange: (next: GitSettings) => void;
  paths: HistoryPaths;
  onPathsChange: (next: HistoryPaths) => void;
  fallback: Fallbacks;
  /** The folder whose branches are offered; '' is the root, null before one is chosen. */
  repositoryPath: string | null;
}) {
  const set = <K extends keyof GitSettings>(key: K, v: GitSettings[K]) => onGitChange({ ...git, [key]: v });
  const followed = fallback.includeGlobs;
  const shownPaths = paths.followPaths && followed ? followed.value.join(', ') : paths.paths;

  return (
    <>
      <Section title="Which commits">
        <GitRefPicker value={git.ref} onChange={(ref) => set('ref', ref)} repositoryPath={repositoryPath} />

        <Field
          label="Paths"
          hint="Comma separated. Only commits that touched them, with only their part of the stat and diff. Empty takes the whole repository."
          aside={followed && (
            <UseDefault field="Paths" from={followed.from} checked={paths.followPaths}
              onChange={(v) => onPathsChange({ ...paths, followPaths: v })} />
          )}
        >
          <Input
            className="mono"
            value={shownPaths}
            disabled={paths.followPaths && !!followed}
            onChange={(e) => onPathsChange({ ...paths, paths: e.target.value })}
            placeholder={paths.followPaths && followed ? 'the whole repository' : 'src/**, docs/**'}
          />
        </Field>

        <Field label="Newest commits only" hint="Counted from the tip. Empty for every commit.">
          <Input
            type="number"
            min={1}
            value={git.maxCommits ?? ''}
            onChange={(e) => {
              const maxCommits = e.target.value === '' ? null : Number(e.target.value);
              // Keeping means nothing without a limit, and the server refuses it there.
              onGitChange({ ...git, maxCommits, keepIndexed: maxCommits == null ? false : git.keepIndexed });
            }}
          />
        </Field>

        {git.maxCommits != null && (
          <CheckField
            label="Keep commits once indexed"
            hint="Off, the limit is a window: each new commit pushes the oldest out. On, it sets how far back the first pass reaches, and indexed commits stay while the ref reaches them."
            checked={git.keepIndexed}
            onChange={(v) => set('keepIndexed', v)}
          />
        )}

        <Field label="Committed since" hint="From 00:00 UTC on this date. Empty for every commit.">
          <Input type="date" value={git.since ?? ''} onChange={(e) => set('since', e.target.value || null)} />
        </Field>

        <CheckField
          label="Merge commits"
          hint="Their default patch is empty and their message is usually generated."
          checked={git.includeMerges}
          onChange={(v) => set('includeMerges', v)}
        />
      </Section>

      {/* Each hint says what its own setting adds, so none of them is wrong when another is
          turned off. The diff's figures are a measurement with the message and the stat
          on, and say so. */}
      <Section title="Each commit's document holds">
        <CheckField
          label="The message"
          hint="Subject and body, which say why a change was made."
          checked={git.includeMessage}
          onChange={(v) => set('includeMessage', v)}
        />
        <CheckField
          label="The stat"
          hint="Which files the commit touched, with ± counts."
          checked={git.includeStat}
          onChange={(v) => set('includeStat', v)}
        />
        <CheckField
          label="The diff"
          hint="The patch. Measured over 201 commits with the message and the stat, a commit was about 2,000 characters without it and about thirteen times that with it."
          checked={git.includeDiff}
          onChange={(v) => set('includeDiff', v)}
        />
        {git.includeDiff && (
          <Field label="Largest diff per commit (KB)" hint="Over it the patch is left out and the document says how large it was. The stat stays.">
            <Input
              type="number"
              min={0}
              step="any"
              value={git.maxDiffBytes / 1024}
              onChange={(e) => set('maxDiffBytes', Math.round(Number(e.target.value) * 1024))}
            />
          </Field>
        )}
      </Section>
    </>
  );
}

/** The include globs a history source's paths send: undefined while they follow. */
const pathsRequest = (p: HistoryPaths) => (p.followPaths ? undefined : globList(p.paths));

// ── Dialogs ─────────────────────────────────────────────────────────────────

const KIND_OPTIONS = [
  { value: 'workspace' as const, label: 'Files' },
  { value: 'githistory' as const, label: 'Commit history' },
];

/**
 * Add a place this corpus takes content from.
 *
 * What the source is comes first, because it decides every field below it: a file source
 * is walked and filtered, a history source is read with git log and has none of the file
 * settings. It was a checkbox after the folder, which read as one more filter and left the
 * reader to work out that half the form had changed underneath it.
 */
export function AddSourceModal({
  corpus,
  initialPath = null,
  onClose,
  onAdded,
}: {
  corpus: Corpus;
  /** Pre-filled when the coverage notice opened this, so the fix is one click from the
   *  warning rather than a path the reader has to retype. '' is the workspace root, for a
   *  gap there; null is nothing chosen yet. */
  initialPath?: string | null;
  onClose: () => void;
  onAdded: () => Promise<void>;
}) {
  const fallback = sourceFallbacks(corpus);

  const [kind, setKind] = useState<Kind>('workspace');
  // Null until something is chosen. The root is the empty string, a choice like any other.
  const [path, setPath] = useState<string | null>(initialPath);
  const [files, setFiles] = useState(() => fileFiltersOf(null, fallback));
  const [git, setGit] = useState<GitSettings>(DEFAULT_GIT);
  const [paths, setPaths] = useState(() => historyPathsOf(null, fallback));
  // In the dialog rather than the page's banner, which sits behind it. A refusal is
  // something to fix here, and the settings it names are the ones still on screen.
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const history = kind === 'githistory';

  // The same folder AND the same kind. Indexing a repository's files and its history is
  // deliberately two sources over one root.
  const alreadyHere = path !== null && corpus.sources.some((s) => s.rootPath === path && s.kind === kind);

  // A file source at the root takes everything under it that no deeper source claims,
  // which on a shared mount can be far more than the loose files a coverage gap names.
  // Said before it is added, and the button names it. A history source at the root is the
  // repository's history and nothing more, so it gets neither.
  const wholeRoot = path === '' && !history;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (path === null || (!history && !capUsable(files))) return;
    setBusy(true);
    setError(null);
    try {
      // A field left on its default is not sent, so the source follows the corpus rather
      // than holding a copy of what the corpus said today. The file settings are not sent
      // for a history source at all: they mean nothing to a commit.
      await api.addSource(corpus.name, history
        ? { workspacePath: path, gitHistory: true, git, includeGlobs: pathsRequest(paths) }
        : { workspacePath: path, ...fileRequest(files) });
      await onAdded();
    } catch (err) {
      setError(err);
      setBusy(false);
    }
  }

  return (
    <Modal title={`Add a source to ${corpus.name}`} onClose={onClose}>
      <ErrorBanner error={error} onDismiss={() => setError(null)} />
      <form onSubmit={submit}>
        <div className="mb-3.5 grid gap-1.5">
          <span className="text-xs font-semibold">What to index</span>
          <Segmented label="What to index" value={kind} onChange={setKind} options={KIND_OPTIONS} className="justify-self-start" />
          <p className="m-0 text-xs text-muted-foreground">
            {history
              ? 'One document per commit. To search a repository’s files and its history, add it once as each.'
              : 'One document per file, read again when the file changes.'}
          </p>
        </div>

        <Field
          label={history ? 'Repository' : 'Folder'}
          hint={history
            ? 'The folder that holds .git. Only folders mounted into the container are listed.'
            : 'Only folders mounted into the container are listed. WORKSPACE_ROOT sets which.'}
        >
          <WorkspacePicker
            value={path}
            onChange={(next) => {
              setPath(next);
              // A branch belongs to the repository it was picked from. Another folder's
              // repository may not have it; the checked-out branch always exists.
              if (next !== path) setGit((g) => ({ ...g, ref: DEFAULT_GIT.ref }));
            }}
            emptyLabel="(choose a folder)"
            disabled={busy}
            reads={history ? 'history' : 'files'}
          />
        </Field>

        {alreadyHere && (
          <Notice tone="warn" className="-mt-2 mb-3.5 text-xs">
            {history
              ? 'This corpus already indexes that folder’s history. Adding it again indexes every commit twice.'
              : 'This corpus already indexes that folder. Adding it again indexes everything twice.'}
          </Notice>
        )}

        {wholeRoot && !alreadyHere && (
          <Notice tone="warn" className="-mt-2 mb-3.5 text-xs">
            A source at the workspace root takes everything under it that no other source in
            this corpus covers: the loose files at the top and every folder without a source of
            its own. Folders another source covers stay with that source.
          </Notice>
        )}

        {history ? (
          <HistoryFields git={git} onGitChange={setGit} paths={paths} onPathsChange={setPaths}
            fallback={fallback} repositoryPath={path} />
        ) : (
          <FileFilterFields value={files} onChange={setFiles} fallback={fallback}
            documentMaxBytes={corpus.configured?.documentMaxBytes ?? null} />
        )}

        <div className="mt-4 flex justify-end gap-2">
          <Button type="button" onClick={onClose}>Cancel</Button>
          <Button variant="primary" type="submit" disabled={path === null || busy || (!history && !capUsable(files))}>
            {busy ? <Spinner /> : <Plus />}
            {wholeRoot ? 'Add the workspace root' : 'Add source'}
          </Button>
        </div>
      </form>
    </Modal>
  );
}

/**
 * Change a file source's filters.
 *
 * Saving queues a refresh only when something moved. Narrowing a filter removes the files
 * it now excludes through the walk's own reconcile: they are simply not seen next pass,
 * which is the path a file deleted from disk already takes.
 */
export function EditSourceModal({ corpus, source, onClose, onSaved }: {
  corpus: Corpus;
  source: Source;
  onClose: () => void;
  onSaved: () => Promise<void>;
}) {
  const fallback = sourceFallbacks(corpus);
  const [files, setFiles] = useState(() => fileFiltersOf(source, fallback));
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!capUsable(files)) return;
    setBusy(true);
    setError(null);
    try {
      // `clear` rather than a null, because the API cannot tell an absent field from an
      // explicit null and would read one as the other.
      const clear = [
        files.followGitignore && 'useGitignore',
        files.followCap && 'maxFileBytes',
        files.followInclude && 'includeGlobs',
        files.followExclude && 'excludeGlobs',
      ].filter((c): c is string => !!c);

      await api.updateSource(corpus.name, source.id, { clear, ...fileRequest(files) });
      await onSaved();
    } catch (err) {
      setError(err);
      setBusy(false);
    }
  }

  return (
    <Modal title={`Edit source ${sourceName(source)}`} onClose={onClose}>
      <ErrorBanner error={error} onDismiss={() => setError(null)} />
      <form onSubmit={submit}>
        <p className="mt-0 mb-3.5 text-sm text-muted-foreground">
          Files under <span className="mono">{sourceName(source)}</span>, one document each.
        </p>

        <FileFilterFields value={files} onChange={setFiles} fallback={fallback}
          documentMaxBytes={corpus.configured?.documentMaxBytes ?? null} />

        <Notice tone="neutral" className="text-xs">
          Saving re-walks the corpus. Files a narrower filter now excludes leave the index;
          nothing on disk is touched.
        </Notice>

        <div className="mt-3.5 flex justify-end gap-2">
          <Button type="button" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={busy || !capUsable(files)}>
            {busy ? <Spinner /> : <Check />}
            Save changes
          </Button>
        </div>
      </form>
    </Modal>
  );
}

/**
 * Change a history source's settings.
 *
 * Saving sends every git setting, because the server replaces the whole set; it queues a
 * refresh only when something moved, and refuses settings git cannot be asked with, which
 * show here rather than as a job failing later.
 */
export function EditHistorySourceModal({ corpus, source, onClose, onSaved }: {
  corpus: Corpus;
  source: Source;
  onClose: () => void;
  onSaved: () => Promise<void>;
}) {
  const fallback = sourceFallbacks(corpus);
  const initial = gitSettingsOf(source.git);
  const [git, setGit] = useState<GitSettings>(initial);
  const [paths, setPaths] = useState(() => historyPathsOf(source, fallback));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  // The paths as they would be applied, so following what is already in force is not a
  // change. Compared sorted, as the content fingerprint compares them: the same paths in
  // another order make the same documents, and the notice below would otherwise warn of a
  // re-read that does not happen.
  const applied = paths.followPaths ? (fallback.includeGlobs?.value ?? []) : globList(paths.paths);
  const sorted = (list: string[]) => [...list].sort().join('\n');
  const pathsChanged = sorted(applied) !== sorted(source.includeGlobs ?? []);

  // Which of the two kinds of change this is, because they cost different amounts:
  // docs/04 has the rule. What a document holds decides every document, so changing it
  // re-reads the history; which commits are selected adds and removes documents and
  // leaves the rest as they are.
  const rewrites = git.includeMessage !== initial.includeMessage
    || git.includeStat !== initial.includeStat
    || git.includeDiff !== initial.includeDiff
    || (git.includeDiff && git.maxDiffBytes !== initial.maxDiffBytes)
    || pathsChanged;

  // Turning keeping off makes the limit a window again, and everything held past it goes
  // on the refresh. A removal the person saving should see coming.
  const dropsKept = initial.keepIndexed && !git.keepIndexed && git.maxCommits != null;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api.updateSource(corpus.name, source.id, {
        git,
        clear: paths.followPaths ? ['includeGlobs'] : [],
        includeGlobs: pathsRequest(paths),
      });
      await onSaved();
    } catch (err) {
      setError(err);
      setBusy(false);
    }
  }

  return (
    <Modal title={`Edit source ${sourceName(source)}`} onClose={onClose}>
      <ErrorBanner error={error} onDismiss={() => setError(null)} />
      <form onSubmit={submit}>
        <p className="mt-0 mb-3.5 text-sm text-muted-foreground">
          The commit history of <span className="mono">{sourceName(source)}</span>, one document per commit.
        </p>

        <HistoryFields git={git} onGitChange={setGit} paths={paths} onPathsChange={setPaths}
          fallback={fallback} repositoryPath={source.rootPath ?? ''} />

        {rewrites ? (
          <Notice tone="warn" className="text-xs">
            Saving re-reads every commit. The message, the stat, the diff and the paths decide
            what each document holds, so every document already indexed changes.
          </Notice>
        ) : (
          <Notice tone="neutral" className="text-xs">
            Saving refreshes the corpus. The ref, the commit limit, the date and merges decide
            which commits are indexed, not what any of them holds: documents already indexed
            are kept, and commits no longer selected leave the index.
          </Notice>
        )}

        {dropsKept && (
          <Notice tone="warn" className="text-xs">
            Without keeping, the limit is a window: commits past the newest{' '}
            {git.maxCommits?.toLocaleString()} leave the index on the refresh.
          </Notice>
        )}

        <div className="mt-3.5 flex justify-end gap-2">
          <Button type="button" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={busy}>
            {busy ? <Spinner /> : <Check />}
            Save changes
          </Button>
        </div>
      </form>
    </Modal>
  );
}

/**
 * Filters every source of this corpus follows unless it sets its own.
 *
 * Followed live rather than copied when a source is added: ten folders under one parent
 * is the case this exists for, and a default that only reached the eleventh would leave
 * the other ten to be edited one at a time, which is the problem it is here to solve.
 */
export function CorpusDefaultsModal({ corpus, onClose, onSaved }: {
  corpus: Corpus;
  onClose: () => void;
  onSaved: () => Promise<void>;
}) {
  const fallback = serverFallbacks(corpus);
  const d = corpus.defaults;

  // The corpus's own values in the source-shaped form, so the same fields draw them.
  const [filters, setFilters] = useState<FileFilters>(() => ({
    ...fileFiltersOf(null, fallback),
    followGitignore: d?.useGitignore == null,
    followCap: d?.maxFileBytes == null,
    useGitignore: d?.useGitignore ?? fallback.useGitignore.value,
    maxFileMb: (d?.maxFileBytes ?? fallback.maxFileBytes.value ?? 0) / MB,
    include: (d?.includeGlobs ?? []).join(', '),
    exclude: (d?.excludeGlobs ?? []).join(', '),
  }));
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  // A source that sets its own value keeps it. Saying how many, rather than how it works,
  // is what stops this reading as "nothing happened".
  const overriding = corpus.sources.filter((s) =>
    s.ownUseGitignore != null || s.ownMaxFileBytes != null
    || s.ownIncludeGlobs != null || s.ownExcludeGlobs != null).length;
  const hasHistory = corpus.sources.some((s) => s.kind === 'githistory');

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!capUsable(filters)) return;
    setBusy(true);
    setError(null);
    try {
      // Null for a field left on the server's setting, and for empty globs: the corpus
      // then says nothing, and sources fall through to the server.
      const include = globList(filters.include);
      const exclude = globList(filters.exclude);
      await api.updateCorpus(corpus.name, {
        defaults: {
          useGitignore: filters.followGitignore ? null : filters.useGitignore,
          maxFileBytes: filters.followCap ? null : Math.round(filters.maxFileMb * MB),
          includeGlobs: include.length ? include : null,
          excludeGlobs: exclude.length ? exclude : null,
        },
      });
      await onSaved();
    } catch (err) {
      setError(err);
      setBusy(false);
    }
  }

  return (
    <Modal title={`Default filters for ${corpus.name}`} onClose={onClose}>
      <ErrorBanner error={error} onDismiss={() => setError(null)} />
      <form onSubmit={submit}>
        <p className="mt-0 mb-3.5 text-sm text-muted-foreground">
          Every source here follows these unless it sets its own.
          {hasHistory && ' Commit-history sources take only Include, as the paths their commits must touch.'}
        </p>

        <FileFilterFields value={filters} onChange={setFilters} fallback={fallback}
          documentMaxBytes={corpus.configured?.documentMaxBytes ?? null} />

        {overriding > 0 && (
          <Notice tone="warn" className="text-xs">
            {overriding === 1
              ? '1 source sets some of its own filters and keeps them.'
              : `${overriding} sources set some of their own filters and keep them.`}{' '}
            Edit a source and tick a field's default to have it follow these.
          </Notice>
        )}

        <div className="mt-3.5 flex justify-end gap-2">
          <Button type="button" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={busy || !capUsable(filters)}>
            {busy ? <Spinner /> : <Check />}
            Save defaults
          </Button>
        </div>
      </form>
    </Modal>
  );
}
