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
const setTokenScopes = vi.fn();
const createToken = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: {
    listTokens: (...a: unknown[]) => listTokens(...a),
    listCorpora: (...a: unknown[]) => listCorpora(...a),
    revokeToken: (...a: unknown[]) => revokeToken(...a),
    setTokenScopes: (...a: unknown[]) => setTokenScopes(...a),
    createToken: (...a: unknown[]) => createToken(...a),
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
  setTokenScopes.mockResolvedValue(token());
  createToken.mockResolvedValue({ token: token(), secret: 'dex_a_b', mcpAddCommand: 'claude mcp add' });
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

/**
 * A key's scopes. Granting configure to an agent that already works should not mean issuing
 * it a new key, and what configure reaches is said where it is granted, because the corpus
 * mapping does not limit it.
 */
describe("a key's scopes", () => {
  it('are changed in place, and the list reloads', async () => {
    const user = userEvent.setup();
    render(<AccessView onError={vi.fn()} />);

    await user.click(await screen.findByRole('button', { name: 'Change the scopes of laptop-agent' }));
    const dialog = await screen.findByRole('dialog', { name: 'What laptop-agent can do' });
    await user.click(within(dialog).getByRole('button', { name: 'configure' }));
    expect(within(dialog).getByText(/reaches the whole workspace/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: /Save/ }));

    expect(setTokenScopes).toHaveBeenCalledWith('t1', ['search', 'configure']);
    await waitFor(() => expect(listTokens).toHaveBeenCalledTimes(2));
  });

  it('cannot be left empty', async () => {
    const user = userEvent.setup();
    render(<AccessView onError={vi.fn()} />);

    await user.click(await screen.findByRole('button', { name: 'Change the scopes of laptop-agent' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'search' }));

    expect(within(dialog).getByText(/needs at least one scope/)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: /Save/ })).toBeDisabled();
  });

  it('are not offered for change on a revoked key', async () => {
    listTokens.mockResolvedValue([token({ revokedUtc: new Date().toISOString() })]);
    render(<AccessView onError={vi.fn()} />);

    expect(await screen.findByText('revoked')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Change the scopes of laptop-agent' })).not.toBeInTheDocument();
  });

  it('include configure when a key is created', async () => {
    const user = userEvent.setup();
    render(<AccessView onError={vi.fn()} />);

    await user.click(await screen.findByRole('button', { name: /New key/ }));
    const dialog = await screen.findByRole('dialog', { name: 'New key' });
    await user.type(within(dialog).getByPlaceholderText('claude-code'), 'setup-agent');
    await user.click(within(dialog).getByRole('button', { name: 'configure' }));
    await user.click(within(dialog).getByRole('button', { name: /Create/ }));

    expect(createToken).toHaveBeenCalledWith('setup-agent', ['search', 'configure'], []);
  });

  /**
   * Asking for a removal is its own scope, so an agent can be given it without being able to
   * configure anything, and the other way round. Nothing about it is ticked for a new key.
   */
  it('offer propose on its own, and never ticked for a new key', async () => {
    const user = userEvent.setup();
    render(<AccessView onError={vi.fn()} />);

    await user.click(await screen.findByRole('button', { name: /New key/ }));
    const dialog = await screen.findByRole('dialog', { name: 'New key' });
    const propose = within(dialog).getByRole('button', { name: 'propose' });
    const configure = within(dialog).getByRole('button', { name: 'configure' });
    expect(propose).toHaveAttribute('aria-pressed', 'false');
    expect(within(dialog).getByText(/each request waits for your decision/)).toBeInTheDocument();

    await user.type(within(dialog).getByPlaceholderText('claude-code'), 'reviewer');
    await user.click(propose);
    expect(configure).toHaveAttribute('aria-pressed', 'false');
    await user.click(within(dialog).getByRole('button', { name: /Create/ }));

    expect(createToken).toHaveBeenCalledWith('reviewer', ['search', 'propose'], []);
  });

  /**
   * Detaching a document is its own scope, separate from adding one. A key that was given ingest
   * can no longer detach unless it also holds destroy, so ingest says so and destroy says what it
   * does, and ticking it warns that nothing asks first.
   */
  it('offer destroy on its own, never ticked for a new key, and warn when it is', async () => {
    const user = userEvent.setup();
    render(<AccessView onError={vi.fn()} />);

    await user.click(await screen.findByRole('button', { name: /New key/ }));
    const dialog = await screen.findByRole('dialog', { name: 'New key' });
    const ingest = within(dialog).getByRole('button', { name: 'ingest' });
    const destroy = within(dialog).getByRole('button', { name: 'destroy' });
    expect(destroy).toHaveAttribute('aria-pressed', 'false');
    expect(within(dialog).getByText(/It cannot detach one: that is destroy/)).toBeInTheDocument();
    expect(within(dialog).queryByText(/nothing asks first. The document stays in the library/)).not.toBeInTheDocument();

    await user.type(within(dialog).getByPlaceholderText('claude-code'), 'cleaner');
    await user.click(destroy);
    expect(ingest).toHaveAttribute('aria-pressed', 'false');
    expect(within(dialog).getByText(/nothing asks first. The document stays in the library/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: /Create/ }));

    expect(createToken).toHaveBeenCalledWith('cleaner', ['search', 'destroy'], []);
  });

  it('can give an existing key destroy, and the key keeps what it held', async () => {
    const user = userEvent.setup();
    listTokens.mockResolvedValue([token({ scopes: 'search,ingest' })]);
    render(<AccessView onError={vi.fn()} />);

    await user.click(await screen.findByRole('button', { name: 'Change the scopes of laptop-agent' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'destroy' }));
    await user.click(within(dialog).getByRole('button', { name: /Save/ }));

    expect(setTokenScopes).toHaveBeenCalledWith('t1', ['search', 'ingest', 'destroy']);
  });
});
