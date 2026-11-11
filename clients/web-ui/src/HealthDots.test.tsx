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
    changedModels: [],
    changedFramings: [],
    ...over,
  } as Health;
}

const open = async (h: Health) => {
  const user = userEvent.setup();
  render(<HealthDots health={h} connected stale={false} />);
  await user.click(screen.getByRole('button'));
};

describe('the dependency panel', () => {
  it('fits a phone: a width from the viewport and the bar, a fixed one only from sm up', async () => {
    // jsdom lays nothing out, so this holds the classes that did it. A fixed 330px box anchored to the right of
    // its button sat 84px off the left edge of a 375px screen, where the wrapped bar leaves the button. Below sm
    // the wrapper is not positioned, so the panel is placed against the bar and takes its width from two insets.
    const { container } = render(<HealthDots health={health()} connected stale={false} />);
    await userEvent.setup().click(screen.getByRole('button'));

    const panel = container.querySelector('.card.absolute');
    expect(panel).not.toBeNull();
    const classes = (panel as HTMLElement).className.split(/\s+/);
    expect(classes).toEqual(expect.arrayContaining(['left-4', 'right-4', 'max-w-md', 'sm:w-[330px]', 'sm:right-0', 'sm:left-auto']));
    // No bare width, which is what overflowed.
    expect(classes).not.toContain('w-[330px]');
    // The wrapper is the containing block only from sm up.
    expect((container.firstElementChild as HTMLElement).className.split(/\s+/)).toContain('sm:relative');
    expect((container.firstElementChild as HTMLElement).className.split(/\s+/)).not.toContain('relative');
  });

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

  it('flags a chunk set whose model no longer answers as it did', async () => {
    await open(health({
      changedModels: [{
        set: 'books:fine', provider: 'ollama', model: 'nomic-embed-text',
        similarity: 0.93, checkedUtc: '2026-11-10T06:00:00Z',
      }],
    }));

    const alert = screen.getByRole('status');
    expect(alert).toHaveTextContent('books:fine');
    expect(alert).toHaveTextContent('nomic-embed-text');
    expect(alert).toHaveTextContent('similarity 0.930');
    // The way out is a control the corpus page has, so the notice names it.
    expect(alert).toHaveTextContent('full reindex of its corpus');
  });

  it('marks the collapsed button for a changed model too', async () => {
    render(<HealthDots
      health={health({
        changedModels: [{
          set: 'books:fine', provider: 'ollama', model: 'nomic-embed-text',
          similarity: 0, checkedUtc: '2026-11-10T06:00:00Z',
        }],
      })}
      connected
      stale={false}
    />);

    expect(within(screen.getByRole('button')).getByText(/1 chunk set embedded by a model that has changed/))
      .toBeInTheDocument();
  });

  it('flags a chunk set embedded under another document framing', async () => {
    await open(health({
      changedFramings: [{
        set: 'books:fine', provider: 'ollama', model: 'embeddinggemma-2',
        recorded: '{text}', current: 'title: none | text: {text}',
      }],
    }));

    const alert = screen.getByRole('status');
    expect(alert).toHaveTextContent('books:fine');
    expect(alert).toHaveTextContent('embedded with the document framing {text}');
    expect(alert).toHaveTextContent('now frames documents as title: none | text: {text}');
    expect(alert).toHaveTextContent('full reindex of its corpus');
  });

  it('marks the collapsed button for a changed framing too', async () => {
    render(<HealthDots
      health={health({
        changedFramings: [{
          set: 'books:fine', provider: 'ollama', model: 'embeddinggemma-2',
          recorded: '{text}', current: 'title: none | text: {text}',
        }],
      })}
      connected
      stale={false}
    />);

    expect(within(screen.getByRole('button')).getByText(/1 chunk set embedded with a document framing that has changed/))
      .toBeInTheDocument();
  });

  it('says both when a model is missing and another has changed', async () => {
    await open(health({
      missingModels: [{ provider: 'ollama', model: 'gone', sets: ['notes:default'] }],
      changedModels: [{
        set: 'books:fine', provider: 'ollama', model: 'nomic-embed-text',
        similarity: 0.5, checkedUtc: '2026-11-10T06:00:00Z',
      }],
    }));

    expect(screen.getAllByRole('status')).toHaveLength(2);
    expect(screen.getByRole('button')).toHaveAttribute(
      'title', expect.stringMatching(/missing; 1 chunk set embedded by a model that has changed/));
  });

  it('opens against a server that predates the field', async () => {
    // The dev loop is a supported workflow — Vite serving this UI against a container
    // built earlier — so the UI can genuinely be ahead of the API, and it was: reading
    // `.length` off the undefined an older `/healthz` returns threw the moment the panel
    // was opened, taking the page with it. The same trap caught a source's globs once.
    const older = health();
    delete (older as { missingModels?: unknown }).missingModels;
    delete (older as { changedModels?: unknown }).changedModels;
    delete (older as { changedFramings?: unknown }).changedFramings;

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
