import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { HealthDots } from './App';
import type { Health } from './api';

/**
 * The dependency panel behind the status dots.
 *
 * It used to show the configured model and its dimensionality as two badges under a
 * heading naming the backend, which reads as "this is what Dexicon embeds with" — true of
 * nothing, because every chunk set pins its own model and the dimensionality is what
 * chooses its Qdrant collection. Those two values describe only what the NEXT corpus
 * inherits, and the corpus form already says so at the moment it applies.
 *
 * What belongs in a health panel is the inverse, and these are about that: a set whose
 * model the provider no longer has. Both dots stay green, the set still says `ready` and
 * its vectors are still in Qdrant, but nothing can embed a query for it.
 */
function health(over: Partial<Health> = {}): Health {
  return {
    status: 'ok',
    qdrant: { endpoint: 'http://dexicon-qdrant:6334', reachable: true },
    ollama: {
      endpoint: 'http://dexicon-ollama:11434', reachable: true, provider: 'ollama',
      model: 'embeddinggemma', dimensions: 768, error: null,
    },
    corpora: 2,
    activeJob: null,
    missingModels: [],
    ...over,
  } as Health;
}

const open = async (h: Health) => {
  const user = userEvent.setup();
  render(<HealthDots health={h} connected stale={false} />);
  await user.click(screen.getByRole('button'));
};

describe('the dependency panel', () => {
  it('names the provider rather than assuming Ollama', async () => {
    // `provider` has been on the wire all along and the UI hardcoded "ollama", so a
    // deployment defaulting to OpenAI labelled its dot with the wrong backend.
    await open(health({
      ollama: { ...health().ollama, provider: 'openai', endpoint: 'https://api.openai.com/v1' },
    }));

    expect(screen.getByRole('button')).toHaveTextContent('openai');
    expect(screen.getByText('https://api.openai.com/v1')).toBeInTheDocument();
  });

  it('says plainly whether embeddings answered', async () => {
    // This badge used to carry the model name, so the panel never answered the one
    // question it exists for — and colour-coded a model by whether the backend was up.
    await open(health());

    expect(screen.getAllByText('reachable')).toHaveLength(2);
  });

  it('does not present the default model as a property of the deployment', async () => {
    await open(health());

    expect(screen.queryByText(/768d/)).not.toBeInTheDocument();
  });

  it('flags a model a chunk set needs and no longer has', async () => {
    await open(health({
      missingModels: [{ provider: 'ollama', model: 'mxbai-embed-large', sets: ['books:fine', 'notes:default'] }],
    }));

    const alert = screen.getByRole('status');
    expect(alert).toHaveTextContent('mxbai-embed-large');
    // Named, because the fix is per set: rebuild that one, or pull the model back.
    expect(alert).toHaveTextContent('books:fine, notes:default');
  });

  it('marks the collapsed button too, since both dots stay green', async () => {
    // Behind the click it is a fault the screen knows about and does not mention.
    render(<HealthDots
      health={health({ missingModels: [{ provider: 'ollama', model: 'gone', sets: ['books:fine'] }] })}
      connected
      stale={false}
    />);

    expect(within(screen.getByRole('button')).getByText(/1 embedding model missing/)).toBeInTheDocument();
  });

  it('opens against a server that predates the field', async () => {
    // The dev loop is a supported workflow — Vite serving this UI against a container
    // built earlier — so the UI can genuinely be ahead of the API, and it was: reading
    // `.length` off the undefined an older `/healthz` returns threw the moment the panel
    // was opened, taking the page with it. The same trap caught a source's globs once.
    const older = health();
    delete (older as { missingModels?: unknown }).missingModels;

    await open(older);

    expect(screen.getByText('Qdrant')).toBeInTheDocument();
    expect(screen.getAllByText('reachable')).toHaveLength(2);
  });

  it('stays quiet when nothing is missing', async () => {
    // A health panel that cries wolf wastes as much time as one that hides a fault.
    render(<HealthDots health={health()} connected stale={false} />);

    expect(screen.getByRole('button')).toHaveAttribute('title', 'Dependency health');
    expect(screen.queryByText(/missing/i)).not.toBeInTheDocument();
  });
});
