import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import { api, ApiError, getToken, setToken, type Health } from './api';

/**
 * Which session a sign-out ends.
 *
 * The health poll signs the page out on a 401. A poll sent with one session can come back
 * after a new sign-in has stored another, and signing out on it ended the new one: the
 * page cleared it and asked the server to revoke it. Seen live when a sign-in ran while
 * the page was still signing itself out after a restart.
 */
const health = vi.fn();
const signOut = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: {
    health: (...a: unknown[]) => health(...a),
    signOut: (...a: unknown[]) => signOut(...a),
    listCorpora: vi.fn().mockResolvedValue([]),
    listJobs: vi.fn().mockResolvedValue([]),
    // A corpus page that never finishes loading: the test only leaves it.
    getCorpus: () => new Promise(() => {}),
    listFiles: () => new Promise(() => {}),
  },
  subscribeToProgress: () => () => {},
}));

// The two screens that are not under test here, replaced by a line each so the section tabs can be told from them.
vi.mock('./ChunkSets', () => ({ ChunkSetsPanel: () => null, ModelsView: () => <p>Models screen</p> }));
vi.mock('./Searches', () => ({ SearchesView: () => <p>Search history screen</p> }));

const healthy = {
  status: 'ok',
  qdrant: { endpoint: 'http://qdrant', reachable: true },
  ollama: { endpoint: 'http://ollama', reachable: true, provider: 'ollama', model: 'm', dimensions: 768, error: null },
  corpora: 0,
  activeJob: null,
  missingModels: [],
  changedModels: [],
  changedFramings: [],
} as unknown as Health;

beforeEach(() => {
  window.location.hash = '';
  vi.clearAllMocks();
  signOut.mockResolvedValue(undefined);
  setToken('dxs_a');
});

afterEach(() => setToken(null));

/**
 * The Approvals link carries how many requests are waiting, for the administrator who can decide
 * them. The count rides on the health poll the page already makes, and a count of nothing is not
 * shown: a badge that reads zero all week is noise.
 */
describe('the Approvals link', () => {
  it('shows how many requests are waiting', async () => {
    health.mockResolvedValue({ ...healthy, pendingProposals: 3 });
    render(<App />);

    const link = await screen.findByRole('button', { name: /^Approvals/ });
    await waitFor(() => expect(link).toHaveAccessibleName('Approvals, 3 waiting'));
    expect(within(link).getByText('3')).toBeInTheDocument();
  });

  it('shows no count when nothing is waiting, and none for a key', async () => {
    for (const pendingProposals of [0, null, undefined]) {
      health.mockResolvedValue({ ...healthy, pendingProposals });
      const { unmount } = render(<App />);

      const link = await screen.findByRole('button', { name: /^Approvals/ });
      await waitFor(() => expect(health).toHaveBeenCalled());
      expect(link).toHaveAccessibleName('Approvals');
      unmount();
    }
  });
});

describe('signing out', () => {
  it('ignores a 401 for a session that is no longer the stored one', async () => {
    let refuse!: (e: unknown) => void;
    health.mockReturnValueOnce(new Promise((_, reject) => { refuse = reject; }));
    render(<App />);
    await waitFor(() => expect(health).toHaveBeenCalled());

    // A new sign-in stored another session while that poll was out.
    setToken('dxs_b');
    refuse(new ApiError(401, 'Unauthorized'));
    await new Promise((r) => setTimeout(r, 20));

    expect(signOut).not.toHaveBeenCalled();
    expect(getToken()).toBe('dxs_b');
    expect(screen.getByRole('button', { name: /sign out/i })).toBeInTheDocument();
  });

  it('signs out on a 401 for the stored session, and ends that session', async () => {
    health.mockRejectedValue(new ApiError(401, 'Unauthorized'));
    render(<App />);

    expect(await screen.findByLabelText('Admin password')).toBeInTheDocument();
    expect(signOut).toHaveBeenCalledWith('dxs_a');
    expect(getToken()).toBeNull();
  });

  it('ends the stored session from the Sign out button', async () => {
    health.mockResolvedValue(healthy);
    render(<App />);

    await userEvent.click(await screen.findByRole('button', { name: /sign out/i }));

    expect(signOut).toHaveBeenCalledWith('dxs_a');
    expect(await screen.findByLabelText('Admin password')).toBeInTheDocument();
    expect(getToken()).toBeNull();
  });
});

/**
 * Activity and Settings each stand for a section of screens. The addresses stay as they were (#/jobs, #/searches,
 * #/models, #/settings), so a link made before the regrouping still opens the screen it named.
 */
describe('the navigation', () => {
  const nav = () => screen.getByRole('navigation');

  it('lists Activity and Settings, and not the screens inside them', async () => {
    health.mockResolvedValue(healthy);
    render(<App />);

    await within(nav()).findByRole('button', { name: 'Activity' });
    const names = within(nav()).getAllByRole('button').map((b) => b.textContent);
    expect(names).toEqual(['Search', 'Corpora', 'Documents', 'Activity', 'Approvals', 'Access', 'Settings']);
  });

  it('opens Jobs from Activity, moves to Search history by its tab, and keeps Activity current on both', async () => {
    health.mockResolvedValue(healthy);
    const user = userEvent.setup();
    render(<App />);

    await user.click(await within(nav()).findByRole('button', { name: 'Activity' }));

    expect(window.location.hash).toBe('#/jobs');
    expect(within(nav()).getByRole('button', { name: 'Activity' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Jobs' })).toHaveAttribute('aria-selected', 'true');

    await user.click(screen.getByRole('tab', { name: 'Search history' }));

    expect(window.location.hash).toBe('#/searches');
    expect(screen.getByText('Search history screen')).toBeInTheDocument();
    expect(within(nav()).getByRole('button', { name: 'Activity' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Search history' })).toHaveAttribute('aria-selected', 'true');
  });

  it('opens General from Settings, moves to Models by its tab, and keeps Settings current on both', async () => {
    health.mockResolvedValue(healthy);
    const user = userEvent.setup();
    render(<App />);

    await user.click(await within(nav()).findByRole('button', { name: 'Settings' }));

    expect(window.location.hash).toBe('#/settings');
    expect(screen.getByRole('tab', { name: 'General' })).toHaveAttribute('aria-selected', 'true');

    await user.click(screen.getByRole('tab', { name: 'Models' }));

    expect(window.location.hash).toBe('#/models');
    expect(screen.getByText('Models screen')).toBeInTheDocument();
    expect(within(nav()).getByRole('button', { name: 'Settings' })).toHaveAttribute('aria-current', 'page');
  });

  it.each([
    ['#/jobs', 'Activity', 'Jobs'],
    ['#/searches', 'Activity', 'Search history'],
    ['#/models', 'Settings', 'Models'],
    ['#/settings', 'Settings', 'General'],
  ])('opens %s on a link made before the regrouping', async (hash, section, tab) => {
    health.mockResolvedValue(healthy);
    window.location.hash = hash;
    render(<App />);

    expect(await screen.findByRole('tab', { name: tab })).toHaveAttribute('aria-selected', 'true');
    expect(within(nav()).getByRole('button', { name: section })).toHaveAttribute('aria-current', 'page');
  });

  it('leaves a corpus page for Activity by its chip', async () => {
    health.mockResolvedValue(healthy);
    window.location.hash = '#/corpora/books';
    const user = userEvent.setup();
    render(<App />);

    await user.click(await within(nav()).findByRole('button', { name: 'Activity' }));

    expect(window.location.hash).toBe('#/jobs');
  });

  it('shows no tabs on a screen that is not part of a section', async () => {
    health.mockResolvedValue(healthy);
    window.location.hash = '#/access';
    render(<App />);

    await within(nav()).findByRole('button', { name: 'Access' });
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
  });
});

describe('before the corpus list has arrived', () => {
  it('says it is loading, and not that there are no corpora', async () => {
    health.mockResolvedValue(healthy);
    vi.mocked(api.listCorpora).mockReturnValueOnce(new Promise(() => {}));
    render(<App />);

    expect(await screen.findByText('Loading…')).toBeInTheDocument();
    expect(screen.queryByText(/No corpora yet/)).not.toBeInTheDocument();
  });

  it('draws the screen once the list has arrived', async () => {
    health.mockResolvedValue(healthy);
    render(<App />);

    expect(await screen.findByText(/No corpora yet/)).toBeInTheDocument();
    expect(screen.queryByText('Loading…')).not.toBeInTheDocument();
  });

  it('draws the screen when the list could not be read, with the error', async () => {
    health.mockResolvedValue(healthy);
    vi.mocked(api.listCorpora).mockRejectedValueOnce(new Error('refused'));
    render(<App />);

    expect(await screen.findByText(/refused/)).toBeInTheDocument();
    expect(screen.queryByText('Loading…')).not.toBeInTheDocument();
  });
});
