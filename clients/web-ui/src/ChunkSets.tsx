import { useEffect, useState } from 'react';
import {
  api,
  getToken,
  type ChunkSet,
  type Corpus,
  type EmbeddingModelInfo,
  type EmbeddingProviderInfo,
  type ModelCapabilities,
  type ModelPullEvent,
} from './api';
import { Badge, CopyButton, ErrorBanner, Field, Modal, Spinner, formatBytes, localTime, relativeTime, stateTone } from './ui';

/**
 * Chunk sets for one corpus.
 *
 * The screen exists to make the SAFE migration obvious and the unsafe one awkward.
 * Adding a set never touches what search returns; promoting it does, once, and only
 * when it is complete. So "Add" is an ordinary button and "Promote" is the one that
 * announces what it is about to change.
 */
export function ChunkSetsPanel({ corpus, onChanged }: { corpus: Corpus; onChanged: () => void }) {
  const [adding, setAdding] = useState(false);
  const [editing, setEditing] = useState<ChunkSet | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);

  const act = async (id: string, fn: () => Promise<unknown>) => {
    setBusy(id);
    setError(null);
    try {
      await fn();
      onChanged();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  };

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '0.6rem' }}>
        <div>
          <strong style={{ fontSize: '0.9rem' }}>Chunk sets</strong>
          <span className="dim" style={{ fontSize: '0.78rem', marginLeft: '0.5rem' }}>
            each is a model and a chunking; search reaches the default one
          </span>
        </div>
        <button className="btn" onClick={() => setAdding(true)}>+ Add set</button>
      </div>

      <ErrorBanner error={error} onDismiss={() => setError(null)} />

      <div style={{ display: 'grid', gap: '0.5rem' }}>
        {corpus.chunkSets.map((set) => (
          <div key={set.id} className="card" style={{ padding: '0.7rem 0.9rem' }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', gap: '1rem', alignItems: 'start' }}>
              <div style={{ minWidth: 0 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', flexWrap: 'wrap' }}>
                  <span className="mono" style={{ fontWeight: 600 }}>
                    {corpus.name}:{set.name}
                  </span>
                  {set.isDefault && <Badge tone="accent">default</Badge>}
                  <Badge tone={stateTone(set.state)}>{set.state}</Badge>
                  {set.pendingCount > 0 && (
                    <Badge tone="warn">
                      {set.pendingCount.toLocaleString()} pending
                    </Badge>
                  )}
                  {set.failedCount > 0 && <Badge tone="danger">{set.failedCount} failed</Badge>}
                </div>

                {set.description && (
                  <p className="dim" style={{ margin: '0.3rem 0 0', fontSize: '0.8rem' }}>{set.description}</p>
                )}

                <div className="dim" style={{ fontSize: '0.78rem', marginTop: '0.35rem' }}>
                  <span className="mono">{set.embeddingProvider}/{set.embeddingModel}</span>{' '}
                  ({set.embeddingDimensions}d) ·{' '}
                  {set.chunkSize} tokens / {set.chunkOverlap} overlap · {set.boundaryMode}
                  {set.unitAware && ' · unit-aware'}
                  {set.sentenceAware && ' · sentence-aware'}
                  {set.headingContext && ' · heading context'}
                </div>

                <div className="dim" style={{ fontSize: '0.78rem', marginTop: '0.2rem' }}>
                  {set.fileCount.toLocaleString()} files · {set.chunkCount.toLocaleString()} chunks ·{' '}
                  <span title={set.lastIndexedUtc ? localTime(set.lastIndexedUtc) : undefined}>
                    {set.lastIndexedUtc ? `indexed ${relativeTime(set.lastIndexedUtc)}` : 'never indexed'}
                  </span>
                </div>
              </div>

              <div style={{ display: 'flex', gap: '0.35rem', flexShrink: 0 }}>
                <CopyButton text={`${corpus.name}:${set.name}`} label="Copy name" />
                <button className="btn" onClick={() => setEditing(set)}>Edit</button>

                {!set.isDefault && (
                  <button
                    className="btn"
                    disabled={busy === set.id || set.pendingCount > 0}
                    // Disabled rather than hidden while work is outstanding: the reason is
                    // the point, and a button that vanishes teaches nothing.
                    title={
                      set.pendingCount > 0
                        ? `${set.pendingCount.toLocaleString()} file(s) still to index — promoting now would make search incomplete`
                        : 'Make this the set that search uses'
                    }
                    onClick={() => act(set.id, () => api.promoteChunkSet(corpus.name, set.name))}
                  >
                    {busy === set.id ? <Spinner /> : 'Promote'}
                  </button>
                )}

                {!set.isDefault && corpus.chunkSets.length > 1 && (
                  <button
                    className="btn"
                    disabled={busy === set.id}
                    onClick={() => {
                      if (!confirm(`Delete chunk set "${set.name}" and its ${set.chunkCount.toLocaleString()} chunks?`))
                        return;
                      void act(set.id, () => api.deleteChunkSet(corpus.name, set.name));
                    }}
                  >
                    Delete
                  </button>
                )}
              </div>
            </div>
          </div>
        ))}
      </div>

      {adding && (
        <ChunkSetModal
          corpus={corpus}
          onClose={() => setAdding(false)}
          onSaved={() => {
            setAdding(false);
            onChanged();
          }}
        />
      )}

      {editing && (
        <ChunkSetModal
          corpus={corpus}
          existing={editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            onChanged();
          }}
        />
      )}
    </div>
  );
}

function ChunkSetModal({
  corpus,
  existing,
  onClose,
  onSaved,
}: {
  corpus: Corpus;
  existing?: ChunkSet;
  onClose: () => void;
  onSaved: () => void;
}) {
  // Inherit from the default set, so "the same but finer" is two edits rather than six.
  const template = existing ?? corpus.chunkSets.find((s) => s.isDefault) ?? corpus.chunkSets[0];

  const [name, setName] = useState(existing?.name ?? '');
  const [description, setDescription] = useState(existing?.description ?? '');
  const [provider, setProvider] = useState(template?.embeddingProvider ?? 'ollama');
  const [model, setModel] = useState(template?.embeddingModel ?? '');
  const [chunkSize, setChunkSize] = useState(template?.chunkSize ?? 768);
  const [chunkOverlap, setChunkOverlap] = useState(template?.chunkOverlap ?? 100);
  const [boundaryMode, setBoundaryMode] = useState(template?.boundaryMode ?? 'language-aware');
  const [pattern, setPattern] = useState(template?.customBoundaryPattern ?? '');
  const [unitAware, setUnitAware] = useState(template?.unitAware ?? false);
  const [sentenceAware, setSentenceAware] = useState(template?.sentenceAware ?? false);
  const [headingContext, setHeadingContext] = useState(template?.headingContext ?? false);

  const [providers, setProviders] = useState<EmbeddingProviderInfo[]>([]);
  const [models, setModels] = useState<EmbeddingModelInfo[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    api.listEmbeddingProviders().then((r) => setProviders(r.providers)).catch(() => setProviders([]));
  }, []);

  useEffect(() => {
    // Best effort, and per provider: a picker is nicer than a text box, but a backend
    // being unreachable must not stop someone editing chunk settings that have nothing
    // to do with it.
    api.listEmbeddingModels(provider).then((r) => setModels(r.models)).catch(() => setModels([]));
  }, [provider]);

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      if (existing) {
        await api.updateChunkSet(corpus.name, existing.name, {
          description,
          chunkSize,
          chunkOverlap,
          boundaryMode,
          customBoundaryPattern: boundaryMode === 'custom' ? pattern : null,
          unitAware,
          sentenceAware,
          headingContext,
        });
      } else {
        await api.createChunkSet(corpus.name, {
          name,
          description,
          embeddingProvider: provider,
          embeddingModel: model,
          chunkSize,
          chunkOverlap,
          boundaryMode,
          customBoundaryPattern: boundaryMode === 'custom' ? pattern : null,
          unitAware,
          sentenceAware,
          headingContext,
        });
      }
      onSaved();
    } catch (e) {
      setError(e);
      setSaving(false);
    }
  };

  const changesModel =
    !existing && (model !== template?.embeddingModel || provider !== template?.embeddingProvider);

  const chosenProvider = providers.find((p) => p.name === provider);

  return (
    <Modal title={existing ? `Edit ${corpus.name}:${existing.name}` : 'Add a chunk set'} onClose={onClose} width={620}>
      <ErrorBanner error={error} onDismiss={() => setError(null)} />

      {!existing && (
        <Field label="Name" hint="Addressed from search as corpus:name. No colons.">
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="fine" autoFocus />
        </Field>
      )}

      <Field label="Description" hint="Optional — what this way of reading the corpus is for.">
        <input value={description} onChange={(e) => setDescription(e.target.value)} />
      </Field>

      {!existing && providers.length > 0 && (
        <Field label="Provider" hint="Which backend embeds this set. Credentials come from configuration, never from here.">
          <select value={provider} onChange={(e) => setProvider(e.target.value)}>
            {providers.map((p) => (
              <option key={p.name} value={p.name} disabled={!p.configured}>
                {p.name} ({p.kind}){p.configured ? '' : ' — not configured'}
              </option>
            ))}
          </select>
        </Field>
      )}

      {!existing && chosenProvider?.detail && (
        <p style={{ color: 'var(--warn)', fontSize: '0.78rem', marginTop: '-0.5rem' }}>
          {chosenProvider.detail}
        </p>
      )}

      {!existing && (
        <Field
          label="Embedding model"
          hint="Pinned once the set exists: a different model is a different vector space, so changing it means a new set."
        >
          {models.length > 0 ? (
            <select value={model} onChange={(e) => setModel(e.target.value)}>
              {!models.some((m) => m.name === model) && <option value={model}>{model}</option>}
              {models.map((m) => (
                <option key={m.name} value={m.name}>
                  {m.name} ({formatBytes(m.sizeBytes)}){m.inUse ? ' · in use' : ''}
                </option>
              ))}
            </select>
          ) : (
            <input value={model} onChange={(e) => setModel(e.target.value)} />
          )}
        </Field>
      )}

      {existing && (
        <p className="dim" style={{ fontSize: '0.78rem', marginTop: 0 }}>
          <span className="mono">{existing.embeddingProvider}/{existing.embeddingModel}</span> is fixed for this
          set. To move to another model or provider, add a set on it and promote once it has built.
        </p>
      )}

      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.75rem' }}>
        <Field label="Chunk size (tokens)" hint="64–8192. Roughly four characters each.">
          <input
            type="number"
            value={chunkSize}
            min={64}
            max={8192}
            onChange={(e) => setChunkSize(Number(e.target.value))}
          />
        </Field>
        <Field label="Overlap (tokens)" hint="Must be smaller than the chunk size.">
          <input
            type="number"
            value={chunkOverlap}
            min={0}
            onChange={(e) => setChunkOverlap(Number(e.target.value))}
          />
        </Field>
      </div>

      <Field label="Boundary mode" hint="Size decides when to split; the boundary decides where.">
        <select value={boundaryMode} onChange={(e) => setBoundaryMode(e.target.value)}>
          <option value="language-aware">language-aware — member and declaration boundaries</option>
          <option value="blank-line">blank-line — paragraphs</option>
          <option value="none">none — size only</option>
          <option value="custom">custom — your own regex</option>
        </select>
      </Field>

      {boundaryMode === 'custom' && (
        <Field label="Boundary pattern" hint="A .NET regex, matched per line. Rejected here if it will not compile.">
          <input className="mono" value={pattern} onChange={(e) => setPattern(e.target.value)} placeholder="^## " />
        </Field>
      )}

      <fieldset style={{ border: '1px solid var(--border)', borderRadius: 8, padding: '0.7rem 0.9rem' }}>
        <legend style={{ fontSize: '0.8rem', fontWeight: 600, padding: '0 0.3rem' }}>Meaning</legend>

        <Toggle
          checked={headingContext}
          onChange={setHeadingContext}
          label="Heading context"
          hint="Embed each chunk under its heading trail, so its vector knows the section it came from. Stored text stays verbatim."
        />
        <Toggle
          checked={unitAware}
          onChange={setUnitAware}
          label="Unit-aware boundaries"
          hint="Split on the document's own structure — page for PDF, chapter for EPUB, slide for PPTX."
        />
        <Toggle
          checked={sentenceAware}
          onChange={setSentenceAware}
          label="Sentence-aware splitting"
          hint="When a split lands mid-paragraph, cut at a sentence rather than a word."
        />
      </fieldset>

      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: '1rem' }}>
        <span className="dim" style={{ fontSize: '0.78rem', maxWidth: 380 }}>
          {existing
            ? 'Saving re-chunks and re-embeds this set. Other sets are untouched.'
            : changesModel
              ? 'Builds alongside the current default. Search is unaffected until you promote it.'
              : 'Builds in the background. Search keeps using the default set until you promote this one.'}
        </span>
        <div style={{ display: 'flex', gap: '0.5rem' }}>
          <button className="btn" onClick={onClose}>Cancel</button>
          <button
            className="btn primary"
            disabled={saving || (!existing && name.trim().length === 0)}
            onClick={() => void save()}
          >
            {saving ? <Spinner /> : existing ? 'Save and re-chunk' : 'Add set'}
          </button>
        </div>
      </div>
    </Modal>
  );
}

function Toggle({
  checked,
  onChange,
  label,
  hint,
}: {
  checked: boolean;
  onChange: (v: boolean) => void;
  label: string;
  hint: string;
}) {
  return (
    <label style={{ display: 'block', marginBottom: '0.6rem', cursor: 'pointer' }}>
      <span style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
        <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
        <span style={{ fontSize: '0.85rem', fontWeight: 600 }}>{label}</span>
      </span>
      <span className="dim" style={{ display: 'block', fontSize: '0.75rem', marginLeft: '1.6rem' }}>{hint}</span>
    </label>
  );
}

/**
 * Model management.
 *
 * Pulls are gigabytes and minutes, so progress streams rather than the page hanging on
 * a request. Deletion is guarded server-side — a model a chunk set embeds with cannot be
 * removed — and the reason is shown here rather than discovered by trying.
 */
export function ModelsView() {
  const [providers, setProviders] = useState<EmbeddingProviderInfo[]>([]);
  const [provider, setProvider] = useState<string>('');
  const [models, setModels] = useState<EmbeddingModelInfo[]>([]);
  const [managed, setManaged] = useState(true);
  const [configured, setConfigured] = useState('');
  const [note, setNote] = useState<string | undefined>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);

  const [pullName, setPullName] = useState('');
  const [pull, setPull] = useState<ModelPullEvent | null>(null);

  const [probing, setProbing] = useState<string | null>(null);
  const [probed, setProbed] = useState<Record<string, ModelCapabilities>>({});
  const [editingProfile, setEditingProfile] = useState<EmbeddingModelInfo | null>(null);

  useEffect(() => {
    api
      .listEmbeddingProviders()
      .then((r) => {
        setProviders(r.providers);
        setProvider((current) => current || r.default);
      })
      .catch((e) => setError(e));
  }, []);

  const refresh = async (name = provider) => {
    if (!name) return;
    try {
      const r = await api.listEmbeddingModels(name);
      setModels(r.models);
      setManaged(r.managed);
      setConfigured(r.configured);
      setNote(r.note);
      setError(null);
    } catch (e) {
      setError(e);
      setModels([]);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    if (provider) void refresh(provider);
  }, [provider]);

  const startPull = async () => {
    const model = pullName.trim();
    if (!model) return;

    setPull({ model, status: 'starting' });
    setError(null);

    try {
      // fetch rather than EventSource: EventSource cannot send an Authorization header,
      // and the token deliberately does not live in a cookie.
      const response = await fetch('/api/embedding-models/pull', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${getToken() ?? ''}` },
        body: JSON.stringify({ model, provider }),
      });

      if (!response.ok || !response.body) throw new Error(`Pull failed: ${response.status}`);

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';

      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const frames = buffer.split('\n\n');
        buffer = frames.pop() ?? '';

        for (const frame of frames) {
          const line = frame.split('\n').find((l) => l.startsWith('data: '));
          if (!line) continue;
          const event = JSON.parse(line.slice(6)) as ModelPullEvent;
          setPull(event);
          if (event.error) setError(new Error(event.error));
        }
      }

      setPullName('');
      await refresh();
    } catch (e) {
      setError(e);
    } finally {
      setTimeout(() => setPull(null), 2000);
    }
  };

  const probe = async (model: string) => {
    setProbing(model);
    setError(null);
    try {
      const capabilities = await api.probeEmbeddingModel(model, provider);
      setProbed((all) => ({ ...all, [model]: capabilities }));
    } catch (e) {
      setError(e);
    } finally {
      setProbing(null);
    }
  };

  const remove = async (model: string) => {
    if (!confirm(`Delete ${model} from ${provider}? Its files are removed from the shared volume.`)) return;
    try {
      await api.deleteEmbeddingModel(model, provider);
      await refresh();
    } catch (e) {
      setError(e);
    }
  };

  const current = providers.find((p) => p.name === provider);

  return (
    <div style={{ display: 'grid', gap: '1rem' }}>
      <div>
        <h1 style={{ margin: 0, fontSize: '1.15rem' }}>Embedding models</h1>
        <p className="dim" style={{ margin: '0.3rem 0 0', fontSize: '0.85rem' }}>
          A chunk set picks a provider and a model. The model's dimensionality decides which Qdrant collection the
          set lives in, so changing it means a new set rather than an edit.
        </p>
      </div>

      <ErrorBanner error={error} onDismiss={() => setError(null)} />

      {providers.length > 1 && (
        <div style={{ display: 'flex', gap: '0.4rem', flexWrap: 'wrap' }}>
          {providers.map((p) => (
            <button
              key={p.name}
              className="btn"
              style={
                p.name === provider
                  ? { borderColor: 'var(--accent)', color: 'var(--accent)' }
                  : undefined
              }
              title={p.detail ?? `${p.kind}${p.managed ? ', models can be pulled' : ', fixed catalogue'}`}
              onClick={() => setProvider(p.name)}
            >
              {p.name}
              {!p.configured && ' ⚠'}
            </button>
          ))}
        </div>
      )}

      {current?.detail && (
        <div className="card" style={{ padding: '0.7rem 0.9rem', borderColor: 'var(--warn)' }}>
          <span style={{ color: 'var(--warn)', fontSize: '0.85rem' }}>{current.detail}</span>
          <p className="dim" style={{ margin: '0.3rem 0 0', fontSize: '0.78rem' }}>
            Credentials come from the environment, never from the catalogue — a chunk set records which provider to
            use, not how to authenticate to it.
          </p>
        </div>
      )}

      {managed && (
        <div className="card" style={{ padding: '0.9rem 1rem' }}>
          <Field
            label="Pull a model"
            hint="An Ollama model name, e.g. mxbai-embed-large. Several hundred megabytes to a few gigabytes."
          >
            <div style={{ display: 'flex', gap: '0.5rem' }}>
              <input
                value={pullName}
                onChange={(e) => setPullName(e.target.value)}
                placeholder="mxbai-embed-large"
                disabled={pull !== null}
                onKeyDown={(e) => e.key === 'Enter' && void startPull()}
              />
              <button className="btn primary" disabled={pull !== null || !pullName.trim()} onClick={() => void startPull()}>
                {pull ? <Spinner /> : 'Pull'}
              </button>
            </div>
          </Field>

          {pull && (
            <div style={{ marginTop: '0.4rem' }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: '0.78rem' }}>
                <span className="mono">{pull.model}</span>
                <span className="dim">
                  {pull.status}
                  {pull.total ? ` · ${formatBytes(pull.completed ?? 0)} / ${formatBytes(pull.total)}` : ''}
                </span>
              </div>
              <div style={{ height: 6, background: 'var(--border)', borderRadius: 3, marginTop: 4, overflow: 'hidden' }}>
                <div
                  style={{
                    width: `${pull.percent ?? 0}%`,
                    height: '100%',
                    background: pull.done ? 'var(--ok)' : 'var(--accent)',
                    transition: 'width 200ms linear',
                  }}
                />
              </div>
            </div>
          )}
        </div>
      )}

      {editingProfile && (
        <FramingModal
          provider={provider}
          model={editingProfile}
          onClose={() => setEditingProfile(null)}
          onSaved={async () => {
            setEditingProfile(null);
            await refresh();
          }}
        />
      )}

      {loading ? (
        <p className="dim">Loading…</p>
      ) : (
        <div className="card" style={{ padding: 0, overflow: 'hidden' }}>
          {note && (
            <p style={{ margin: 0, padding: '0.8rem 1rem', fontSize: '0.83rem', color: 'var(--warn)' }}>{note}</p>
          )}

          {models.map((m) => {
            const caps = probed[m.name];
            return (
              <div key={m.name} style={{ padding: '0.7rem 1rem', borderTop: '1px solid var(--border)' }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '1rem' }}>
                  <div>
                    <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', flexWrap: 'wrap' }}>
                      <span className="mono" style={{ fontWeight: 600 }}>{m.name}</span>
                      {m.inUse && <Badge tone="accent">in use</Badge>}
                      {m.name.replace(/:latest$/, '') === configured.replace(/:latest$/, '') && (
                        <Badge tone="ok">default for new corpora</Badge>
                      )}
                    </div>
                    <div className="dim" style={{ fontSize: '0.78rem', marginTop: '0.2rem' }}>
                      {m.sizeBytes > 0 ? formatBytes(m.sizeBytes) : provider}
                      {m.dimensions ? ` · ${m.dimensions} dimensions` : ' · dimensions unknown until first use'}
                    </div>

                    {/* Framing is the setting nobody thinks to ask about and the one that
                        silently costs recall, so it is stated on every row rather than
                        hidden behind the editor. */}
                    <div style={{ fontSize: '0.78rem', marginTop: '0.3rem' }}>
                      {m.templateOrigin === 'none' ? (
                        <span style={{ color: 'var(--warn)' }}>
                          embedded raw — no task framing for this model
                        </span>
                      ) : (
                        <span className="dim">
                          <span className="mono">{m.queryTemplate}</span>
                          {m.templateOrigin === 'builtin' && ' · built-in default'}
                          {m.templateOrigin === 'configured' && ' · configured here'}
                        </span>
                      )}
                    </div>
                  </div>

                  <div style={{ display: 'flex', gap: '0.35rem' }}>
                    <button
                      className="btn"
                      title="How text is wrapped before it is embedded"
                      onClick={() => setEditingProfile(m)}
                    >
                      Framing
                    </button>

                    <button
                      className="btn"
                      disabled={probing !== null}
                      title="Measure what this model actually accepts, without indexing anything"
                      onClick={() => void probe(m.name)}
                    >
                      {probing === m.name ? <Spinner /> : 'Test limits'}
                    </button>

                    {managed && (
                      <button
                        className="btn"
                        disabled={m.inUse}
                        title={m.inUse ? 'A chunk set embeds with this model. Migrate it first.' : 'Remove from Ollama'}
                        onClick={() => void remove(m.name)}
                      >
                        Delete
                      </button>
                    )}
                  </div>
                </div>

                {caps && (
                  <div
                    className="card"
                    style={{
                      marginTop: '0.6rem',
                      padding: '0.6rem 0.8rem',
                      fontSize: '0.8rem',
                      borderColor: caps.truncatesSilently
                        ? 'color-mix(in oklab, var(--warn) 45%, transparent)'
                        : undefined,
                    }}
                  >
                    <div style={{ display: 'flex', gap: '1.2rem', flexWrap: 'wrap' }}>
                      <span>
                        <strong>{caps.dimensions}</strong> dimensions
                      </span>
                      <span>
                        accepts{' '}
                        <strong>{caps.maxInputChars ? caps.maxInputChars.toLocaleString() : 'unbounded'}</strong> chars
                      </span>
                      <span>
                        suggested chunk size <strong>{caps.recommendedChunkTokens.toLocaleString()}</strong> tokens
                      </span>
                      {caps.truncatesSilently && <Badge tone="warn">truncates silently</Badge>}
                    </div>
                    <p className="dim" style={{ margin: '0.4rem 0 0' }}>{caps.summary}</p>
                    <p className="dim" style={{ margin: '0.3rem 0 0', fontSize: '0.74rem' }}>
                      {caps.embedCalls} embed calls, {(caps.tookMs / 1000).toFixed(1)}s — nothing was indexed.
                    </p>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

/**
 * The task framing for one model.
 *
 * Most embedding models are trained with an instruction wrapped around the input and
 * retrieve measurably worse without it — and nothing fails, so the only symptom is a
 * worse ranking. Built-in defaults cover the models this build knows; this is how you
 * correct one, or configure a model released after it.
 */
function FramingModal({
  provider,
  model,
  onClose,
  onSaved,
}: {
  provider: string;
  model: EmbeddingModelInfo;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [documentTemplate, setDocumentTemplate] = useState(model.documentTemplate);
  const [queryTemplate, setQueryTemplate] = useState(model.queryTemplate);
  const [notes, setNotes] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [result, setResult] = useState<string[] | null>(null);

  const valid =
    documentTemplate.includes('{text}') && queryTemplate.includes('{text}');

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const r = await api.saveModelProfile({
        provider,
        model: model.name,
        documentTemplate,
        queryTemplate,
        notes: notes || undefined,
      });
      // Shown before closing: re-indexing is a consequence people should see coming.
      if (r.reindexing.length > 0) setResult(r.reindexing);
      else onSaved();
    } catch (e) {
      setError(e);
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal title={`Framing — ${model.name}`} onClose={onClose} width={620}>
      <ErrorBanner error={error} onDismiss={() => setError(null)} />

      {result ? (
        <div>
          <p style={{ fontSize: '0.88rem' }}>
            Saved. {result.length} chunk set{result.length === 1 ? '' : 's'} are re-indexing, because
            framing changes the vectors and both sides of a search have to agree:
          </p>
          <ul className="mono" style={{ fontSize: '0.82rem' }}>
            {result.map((s) => <li key={s}>{s}</li>)}
          </ul>
          <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
            <button className="btn primary" onClick={onSaved}>Done</button>
          </div>
        </div>
      ) : (
        <>
          <p className="dim" style={{ fontSize: '0.82rem', marginTop: 0 }}>
            {model.templateOrigin === 'none' && 'This model has no framing: text is embedded exactly as it is. '}
            {model.templateOrigin === 'builtin' && 'Currently using a built-in default. Saving overrides it. '}
            {model.templateOrigin === 'configured' && 'Configured here. '}
            Use <span className="mono">{'{text}'}</span> where the content goes — on its own it means embed unchanged.
          </p>

          <Field label="Indexed text" hint="Applied to every chunk as it is indexed.">
            <input
              className="mono"
              value={documentTemplate}
              onChange={(e) => setDocumentTemplate(e.target.value)}
              placeholder="search_document: {text}"
            />
          </Field>

          <Field label="Search queries" hint="Applied to the query before it is embedded.">
            <input
              className="mono"
              value={queryTemplate}
              onChange={(e) => setQueryTemplate(e.target.value)}
              placeholder="search_query: {text}"
            />
          </Field>

          <Field label="Notes" hint="Optional — where these values came from.">
            <input value={notes} onChange={(e) => setNotes(e.target.value)} placeholder="from the model card" />
          </Field>

          {!valid && (
            <p style={{ color: 'var(--warn)', fontSize: '0.8rem' }}>
              Both templates must contain <span className="mono">{'{text}'}</span>. Without it every input embeds
              as the same constant string.
            </p>
          )}

          {model.inUse && (
            <p className="dim" style={{ fontSize: '0.8rem' }}>
              This model is in use. Saving re-indexes every chunk set on it.
            </p>
          )}

          <div style={{ display: 'flex', gap: '0.5rem', justifyContent: 'flex-end', marginTop: '1rem' }}>
            <button className="btn" onClick={onClose}>Cancel</button>
            <button className="btn primary" disabled={saving || !valid} onClick={() => void save()}>
              {saving ? <Spinner /> : 'Save'}
            </button>
          </div>
        </>
      )}
    </Modal>
  );
}
