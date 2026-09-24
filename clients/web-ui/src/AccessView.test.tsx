import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AccessView } from './App';
import { ApiError, type TokenSummary } from './api';

/**
 * API keys. Revoking one locks an agent out on its next call, and nothing on this screen
 * can undo it, so it asks first and a refusal is shown in the dialog that asked.
 */
const listTokens = vi.fn();
const listCorpora = vi.fn();
const revokeToken = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: {
    listTokens: (...a: unknown[]) => listTokens(...a),
    listCorpora: (...a: unknown[]) => listCorpora(...a),
    revokeToken: (...a: unknown[]) => revokeToken(...a),
  },
}));

function token(over: Partial<TokenSummary> = {}): TokenSummary {
  return {
    id: 't1',
    name: 'laptop-agent',
    scopes: 'search',
    createdUtc: new Date().toISOString(),
    lastUsedUtc: null,
    expiresUtc: null,
    revokedUtc: null,
    corpusIds: [],
    ...over,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  listTokens.mockResolvedValue([token()]);
  listCorpora.mockResolvedValue([]);
  revokeToken.mockResolvedValue(undefined);
});

describe('the key list', () => {
  it('shows each scope on its own, not the stored comma list', async () => {
    listTokens.mockResolvedValue([token({ scopes: 'search,ingest' })]);
    render(<AccessView onError={vi.fn()} />);

    expect(await screen.findByText('ingest')).toBeInTheDocument();
    expect(screen.getByText('search')).toBeInTheDocument();
    expect(screen.queryByText('search,ingest')).not.toBeInTheDocument();
  });
});

describe('revoking a key', () => {
  it('asks first, and revokes nothing on cancel', async () => {
    const user = userEvent.setup();
    render(<AccessView onError={vi.fn()} />);

    await user.click(await screen.findByRole('button', { name: 'Revoke' }));
    const dialog = await screen.findByRole('dialog', { name: 'Revoke laptop-agent?' });
    expect(dialog).toHaveTextContent(/cannot be restored/);
    expect(revokeToken).not.toHaveBeenCalled();

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(revokeToken).not.toHaveBeenCalled();
  });

  it('revokes once confirmed, and reloads the list', async () => {
    const user = userEvent.setup();
    render(<AccessView onError={vi.fn()} />);

    await user.click(await screen.findByRole('button', { name: 'Revoke' }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Revoke key' }));

    expect(revokeToken).toHaveBeenCalledWith('t1');
    await waitFor(() => expect(listTokens).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('says why in the dialog when the server refuses', async () => {
    revokeToken.mockRejectedValue(new ApiError(404, 'Not found', 'No such key.'));
    const onError = vi.fn();
    const user = userEvent.setup();
    render(<AccessView onError={onError} />);

    await user.click(await screen.findByRole('button', { name: 'Revoke' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Revoke key' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(/No such key/);
    expect(onError).not.toHaveBeenCalled();
  });

  it('is not offered on a key already revoked', async () => {
    listTokens.mockResolvedValue([token({ revokedUtc: new Date().toISOString() })]);
    render(<AccessView onError={vi.fn()} />);

    expect(await screen.findByText('revoked')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Revoke' })).not.toBeInTheDocument();
  });
});
