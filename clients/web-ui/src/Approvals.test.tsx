import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApprovalsView, headline, takes } from './Approvals';
import { ApiError, type Proposal } from './api';

/**
 * Removals agents have asked for, decided by a person.
 *
 * Approving removes something for good, so it asks first with the figures the server worked out,
 * and a refusal appears where the person is looking: in the dialog for an approval, on the
 * request for a rejection. What would go is the server's figure and the agent's reason is shown
 * as text beside it.
 */
const listProposals = vi.fn();
const approveProposal = vi.fn();
const rejectProposal = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: {
    listProposals: (...a: unknown[]) => listProposals(...a),
    approveProposal: (...a: unknown[]) => approveProposal(...a),
    rejectProposal: (...a: unknown[]) => rejectProposal(...a),
  },
}));

function proposal(over: Partial<Proposal> = {}): Proposal {
  return {
    id: 'p1',
    createdUtc: new Date(Date.now() - 3 * 60_000).toISOString(),
    keyName: 'research-agent',
    corpusName: 'notes',
    kind: 'source',
    target: 'files:docs',
    reason: 'superseded by the new export',
    status: 'pending',
    decidedUtc: null,
    error: null,
    gone: false,
    facts: { sources: 1, files: 214, chunks: 1912, chunkSets: 2, blocker: null },
    ...over,
  };
}

const onError = vi.fn();
const onDecided = vi.fn();

function show() {
  return render(<ApprovalsView onError={onError} onDecided={onDecided} />);
}

beforeEach(() => {
  vi.clearAllMocks();
  listProposals.mockResolvedValue([proposal()]);
  approveProposal.mockResolvedValue(proposal({ status: 'approved' }));
  rejectProposal.mockResolvedValue(proposal({ status: 'rejected' }));
});

describe('what is waiting', () => {
  it('says what each request would remove, how much, and who asked for it and why', async () => {
    show();

    expect(await screen.findByText('Remove the source files:docs from notes')).toBeInTheDocument();
    expect(screen.getByText(/214 files and 1,912 chunks, across 2 chunk sets/)).toBeInTheDocument();
    expect(screen.getByText('research-agent said')).toBeInTheDocument();
    expect(screen.getByText('superseded by the new export')).toBeInTheDocument();
    expect(screen.getByText(/Asked/)).toBeInTheDocument();
    expect(listProposals).toHaveBeenCalledWith(false);
  });

  it('shows the agent\'s reason as text, not as markup', async () => {
    listProposals.mockResolvedValue([proposal({ reason: '<img src=x onerror=alert(1)> **now**' })]);
    show();

    expect(await screen.findByText('<img src=x onerror=alert(1)> **now**')).toBeInTheDocument();
    expect(document.querySelector('img')).toBeNull();
  });

  it('counts commits for a history source', async () => {
    listProposals.mockResolvedValue([proposal({ target: 'history:repos/app' })]);
    show();

    expect(await screen.findByText(/214 commits and 1,912 chunks/)).toBeInTheDocument();
  });

  it('says plainly that nothing is waiting, and where requests come from', async () => {
    listProposals.mockResolvedValue([]);
    show();

    expect(await screen.findByText('Nothing is waiting')).toBeInTheDocument();
    expect(screen.getByText(/Grant the scope to a key on the Access page/)).toBeInTheDocument();
  });

  it('warns when the target has gone, and when something stands in the way', async () => {
    listProposals.mockResolvedValue([
      proposal({ id: 'gone', gone: true, facts: null }),
      proposal({
        id: 'blocked', kind: 'chunk_set', target: 'alt-1',
        facts: { sources: 0, files: 12, chunks: 300, chunkSets: 1, blocker: 'it is the default chunk set' },
      }),
    ]);
    show();

    expect(await screen.findByText(/no longer there\. Approving records the request as failed/)).toBeInTheDocument();
    expect(screen.getByText(/cannot be approved now: it is the default chunk set/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Approve: Delete the chunk set alt-1/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: /Approve: Remove the source files:docs/ })).toBeEnabled();
  });
});

describe('approving', () => {
  it('asks first, with the figures, and removes nothing until it is confirmed', async () => {
    const user = userEvent.setup();
    show();

    await user.click(await screen.findByRole('button', { name: /Approve: Remove the source/ }));
    const dialog = await screen.findByRole('dialog', { name: 'Remove the source files:docs from notes?' });
    expect(within(dialog).getByText(/This takes 214 files and 1,912 chunks, across 2 chunk sets/)).toBeInTheDocument();
    expect(approveProposal).not.toHaveBeenCalled();

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(approveProposal).not.toHaveBeenCalled();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('approves on confirmation, reloads the list and tells the page something moved', async () => {
    const user = userEvent.setup();
    show();
    await user.click(await screen.findByRole('button', { name: /Approve: Remove the source/ }));
    const dialog = await screen.findByRole('dialog');
    listProposals.mockResolvedValue([]);

    await user.click(within(dialog).getByRole('button', { name: 'Approve and remove' }));

    expect(approveProposal).toHaveBeenCalledWith('p1');
    await waitFor(() => expect(onDecided).toHaveBeenCalledTimes(1));
    expect(await screen.findByText('Nothing is waiting')).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('shows a refusal in the dialog and leaves the dialog open to be read', async () => {
    const user = userEvent.setup();
    approveProposal.mockRejectedValue(new ApiError(
      409, 'Cannot delete a chunk set while it is being indexed', 'A job is working on \'alt-1\'.'));
    show();
    await user.click(await screen.findByRole('button', { name: /Approve: Remove the source/ }));
    const dialog = await screen.findByRole('dialog');

    await user.click(within(dialog).getByRole('button', { name: 'Approve and remove' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(/A job is working on 'alt-1'/);
    expect(within(dialog).getByRole('button', { name: 'Approve and remove' })).toBeEnabled();
    expect(onError).not.toHaveBeenCalled();
    expect(onDecided).not.toHaveBeenCalled();
  });

  it('records a request whose target has gone as failed, and says that before it does', async () => {
    const user = userEvent.setup();
    listProposals.mockResolvedValue([proposal({ gone: true, facts: null })]);
    show();

    await user.click(await screen.findByRole('button', { name: /Approve:/ }));
    const dialog = await screen.findByRole('dialog', { name: 'Record this request as failed?' });

    expect(within(dialog).getByText(/nothing will be removed/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Record as failed' }));
    expect(approveProposal).toHaveBeenCalledWith('p1');
  });
});

describe('rejecting', () => {
  it('rejects without asking, since nothing is lost, and reloads', async () => {
    const user = userEvent.setup();
    show();
    listProposals.mockResolvedValue([]);

    await user.click(await screen.findByRole('button', { name: /Reject: Remove the source/ }));

    expect(rejectProposal).toHaveBeenCalledWith('p1');
    await waitFor(() => expect(onDecided).toHaveBeenCalledTimes(1));
    expect(await screen.findByText('Nothing is waiting')).toBeInTheDocument();
  });

  it('shows a refusal on the request it came from', async () => {
    const user = userEvent.setup();
    rejectProposal.mockRejectedValue(new ApiError(409, 'Already decided', 'This proposal was decided by someone else a moment ago.'));
    listProposals.mockResolvedValue([proposal(), proposal({ id: 'p2', target: 'files:notes' })]);
    show();

    const first = (await screen.findAllByRole('listitem'))[0];
    await user.click(within(first).getByRole('button', { name: /Reject:/ }));

    expect(await within(first).findByRole('alert')).toHaveTextContent(/decided by someone else/);
    expect(within(screen.getAllByRole('listitem')[1]).queryByRole('alert')).not.toBeInTheDocument();
    expect(onError).not.toHaveBeenCalled();
  });
});

describe('what has been decided', () => {
  it('lists the outcomes, with the reason one failed, and offers no action on them', async () => {
    const user = userEvent.setup();
    listProposals.mockImplementation((decided: boolean) => Promise.resolve(decided
      ? [
        proposal({ id: 'a', status: 'approved', decidedUtc: new Date().toISOString(), facts: null }),
        proposal({ id: 'r', target: 'files:notes', status: 'rejected', decidedUtc: new Date().toISOString(), facts: null }),
        proposal({
          id: 'f', target: 'files:old', status: 'failed', decidedUtc: new Date().toISOString(), facts: null,
          error: 'The source is no longer there.',
        }),
      ]
      : [proposal()]));
    show();
    await screen.findByText('Remove the source files:docs from notes');

    await user.click(screen.getByRole('radio', { name: 'Decided' }));

    expect(await screen.findByText('approved')).toBeInTheDocument();
    expect(screen.getByText('rejected')).toBeInTheDocument();
    expect(screen.getByText('failed')).toBeInTheDocument();
    expect(screen.getByText(/it was removed/)).toBeInTheDocument();
    expect(screen.getByText(/it stays/)).toBeInTheDocument();
    expect(screen.getByText(/The source is no longer there\./)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Approve:|Reject:/ })).not.toBeInTheDocument();
    expect(listProposals).toHaveBeenLastCalledWith(true);
  });

  it('says so when nothing has been decided', async () => {
    const user = userEvent.setup();
    listProposals.mockImplementation((decided: boolean) => Promise.resolve(decided ? [] : [proposal()]));
    show();
    await screen.findByText('Remove the source files:docs from notes');

    await user.click(screen.getByRole('radio', { name: 'Decided' }));

    expect(await screen.findByText('Nothing has been decided yet')).toBeInTheDocument();
  });
});

describe('the words for each kind', () => {
  it('names what is removed from where', () => {
    const base = proposal();
    expect(headline({ ...base, kind: 'source', target: 'files:docs' })).toBe('Remove the source files:docs from notes');
    expect(headline({ ...base, kind: 'chunk_set', target: 'alt-1' })).toBe('Delete the chunk set alt-1 from notes');
    expect(headline({ ...base, kind: 'document', target: 'paper.md' })).toBe('Detach the document paper.md from notes');
    expect(headline({ ...base, kind: 'corpus', target: 'notes' })).toBe('Delete the corpus notes');
  });

  it('counts what each kind would take, singular where it is one', () => {
    const facts = { sources: 3, files: 58, chunks: 402, chunkSets: 1, blocker: null };
    const base = proposal({ facts });
    expect(takes({ ...base, kind: 'corpus' })).toBe('3 sources, 58 files and 402 chunks, across 1 chunk set');
    expect(takes({ ...base, kind: 'chunk_set' })).toBe('402 chunks, from 58 files');
    expect(takes({ ...base, kind: 'document', facts: { ...facts, chunks: 1, chunkSets: 2 } })).toBe('1 chunk, across 2 chunk sets');
    expect(takes({ ...base, facts: null })).toBeNull();
  });
});
