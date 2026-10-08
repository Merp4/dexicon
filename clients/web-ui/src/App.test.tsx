import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import { ApiError, getToken, setToken, type Health } from './api';

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
  },
  subscribeToProgress: () => () => {},
}));

const healthy = {
  status: 'ok',
  qdrant: { endpoint: 'http://qdrant', reachable: true },
  ollama: { endpoint: 'http://ollama', reachable: true, provider: 'ollama', model: 'm', dimensions: 768, error: null },
  corpora: 0,
  activeJob: null,
  missingModels: [],
} as unknown as Health;

beforeEach(() => {
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
