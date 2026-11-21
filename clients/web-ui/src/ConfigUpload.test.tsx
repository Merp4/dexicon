import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ConfigUpload, summary } from './ConfigUpload';
import type { ConfigImportResult } from './api';

/**
 * Reading a configuration file into the instance: the file is checked before it is applied, applying asks first, and what each
 * item did or would do is shown with its reason.
 */
const importConfig = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: { importConfig: (...a: unknown[]) => importConfig(...a) },
}));

const file = { format: 'dexicon-config', version: 1, corpora: [] };

function upload(user: ReturnType<typeof userEvent.setup>, content: string, name = 'dexicon-config.json') {
  return user.upload(screen.getByLabelText('Configuration file'), new File([content], name, { type: 'application/json' }));
}

function plan(over: Partial<ConfigImportResult> = {}): ConfigImportResult {
  return {
    applied: false,
    counts: { create: 2, unchanged: 1, refused: 1 },
    items: [
      { kind: 'corpus', name: 'Papers', outcome: 'create', detail: 'Would be made with a chunk set named default.' },
      { kind: 'source', name: 'Papers: workspace notes', outcome: 'create', detail: 'Would be added, and a refresh queued.' },
      { kind: 'corpus', name: 'Notes', outcome: 'unchanged', detail: null },
      { kind: 'source', name: 'Notes: githistory repo', outcome: 'refused', detail: 'Not a git-history source: it is not a repository.' },
    ],
    ...over,
  };
}

beforeEach(() => {
  importConfig.mockReset();
});

describe('the summary', () => {
  it('lists what changes, then what does not, then what went wrong', () => {
    expect(summary({ failed: 1, create: 2, 'not imported': 3, unchanged: 4, refused: 5 }))
      .toBe('2 create, 4 unchanged, 3 not imported, 5 refused, 1 failed');
  });

  it('puts a create before an update and leaves out a count of nothing', () => {
    expect(summary({ update: 1, create: 1 })).toBe('1 create, 1 update');
    expect(summary({ create: 0, update: 1 })).toBe('1 update');
  });

  it('says so when the file held nothing', () => {
    expect(summary({})).toBe('nothing in the file');
  });
});

describe('the import screen', () => {
  it('cannot check or apply before a file is chosen', () => {
    render(<ConfigUpload />);

    expect(screen.getByRole('button', { name: 'Check the file' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();
  });

  it('checks the file as a dry run and lists each item with its outcome and reason', async () => {
    importConfig.mockResolvedValue(plan());
    const user = userEvent.setup();
    render(<ConfigUpload />);

    await upload(user, JSON.stringify(file));
    await user.click(screen.getByRole('button', { name: 'Check the file' }));

    expect(importConfig).toHaveBeenCalledWith(file, false);
    expect(await screen.findByText(/Checked, nothing changed: 2 create, 1 unchanged, 1 refused/)).toBeInTheDocument();
    const rows = within(screen.getByRole('list', { name: 'What the file does' })).getAllByRole('listitem');
    expect(rows).toHaveLength(4);
    expect(within(rows[0]).getByText('create')).toBeInTheDocument();
    expect(within(rows[0]).getByText('Would be made with a chunk set named default.')).toBeInTheDocument();
    expect(within(rows[3]).getByText('refused').className).toMatch(/warn/);
  });

  it('applies only after the file was checked, and asks before it does', async () => {
    importConfig.mockResolvedValueOnce(plan());
    const user = userEvent.setup();
    render(<ConfigUpload />);
    await upload(user, JSON.stringify(file));
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Check the file' }));
    await screen.findByText(/Checked, nothing changed/);
    await user.click(screen.getByRole('button', { name: 'Apply' }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/2 items make a change/)).toBeInTheDocument();
    expect(within(dialog).getByText(/No corpus, source or chunk set is deleted/)).toBeInTheDocument();
    expect(within(dialog).getByText(/a narrower filter\s+removes the files it excludes from the index/)).toBeInTheDocument();
    expect(importConfig).toHaveBeenCalledTimes(1);

    importConfig.mockResolvedValueOnce(plan({ applied: true, counts: { create: 2 } }));
    await user.click(within(dialog).getByRole('button', { name: 'Apply' }));

    await vi.waitFor(() => expect(importConfig).toHaveBeenLastCalledWith(file, true));
    expect(await screen.findByText(/Applied: 2 create\./)).toBeInTheDocument();
  });

  it('does not apply when the question is cancelled', async () => {
    importConfig.mockResolvedValue(plan());
    const user = userEvent.setup();
    render(<ConfigUpload />);
    await upload(user, JSON.stringify(file));
    await user.click(screen.getByRole('button', { name: 'Check the file' }));
    await screen.findByText(/Checked, nothing changed/);

    await user.click(screen.getByRole('button', { name: 'Apply' }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Cancel' }));

    expect(importConfig).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('refuses a file that is not JSON and keeps both buttons off', async () => {
    const user = userEvent.setup();
    render(<ConfigUpload />);

    await upload(user, 'this is not json', 'notes.txt');

    expect(await screen.findByText('notes.txt is not a JSON configuration file.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Check the file' })).toBeDisabled();
    expect(importConfig).not.toHaveBeenCalled();
  });

  it('refuses JSON that is not an object', async () => {
    const user = userEvent.setup();
    render(<ConfigUpload />);

    await upload(user, '[1, 2]');

    expect(await screen.findByText(/is not a JSON configuration file/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Check the file' })).toBeDisabled();
  });

  it('forgets the last result when another file is chosen, so a file is never applied on another file\'s check', async () => {
    importConfig.mockResolvedValue(plan());
    const user = userEvent.setup();
    render(<ConfigUpload />);
    await upload(user, JSON.stringify(file));
    await user.click(screen.getByRole('button', { name: 'Check the file' }));
    await screen.findByText(/Checked, nothing changed/);

    await upload(user, JSON.stringify({ ...file, corpora: [{ name: 'Other' }] }), 'other.json');

    expect(screen.queryByText(/Checked, nothing changed/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();
  });

  it('shows the server\'s refusal of the whole file', async () => {
    importConfig.mockRejectedValue(new Error('Not a configuration file this version reads'));
    const user = userEvent.setup();
    render(<ConfigUpload />);
    await upload(user, JSON.stringify(file));

    await user.click(screen.getByRole('button', { name: 'Check the file' }));

    expect(await screen.findByText(/Not a configuration file this version reads/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();
  });

  it('does not offer to apply a result that was already applied', async () => {
    importConfig.mockResolvedValueOnce(plan());
    const user = userEvent.setup();
    render(<ConfigUpload />);
    await upload(user, JSON.stringify(file));
    await user.click(screen.getByRole('button', { name: 'Check the file' }));
    await screen.findByText(/Checked, nothing changed/);
    importConfig.mockResolvedValueOnce(plan({ applied: true, counts: { create: 2 } }));
    await user.click(screen.getByRole('button', { name: 'Apply' }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Apply' }));
    await screen.findByText(/Applied:/);

    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Check the file' })).toBeEnabled();
  });

  it('keeps the file chooser disabled while a request runs, so a late answer cannot belong to another file', async () => {
    let answer: (r: ConfigImportResult) => void = () => {};
    importConfig.mockReturnValueOnce(new Promise<ConfigImportResult>((resolve) => { answer = resolve; }));
    const user = userEvent.setup();
    render(<ConfigUpload />);
    await upload(user, JSON.stringify(file));

    await user.click(screen.getByRole('button', { name: 'Check the file' }));

    expect(screen.getByLabelText('Configuration file')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Checking…' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();
    answer(plan());
    await screen.findByText(/Checked, nothing changed/);
    expect(screen.getByLabelText('Configuration file')).toBeEnabled();
  });

  it('tones each outcome: changes accent, what is left as it is neutral, a refusal warn and a failure danger', async () => {
    const items = ['create', 'update', 'unchanged', 'not imported', 'extra', 'refused', 'failed'].map((outcome) => ({
      kind: 'corpus', name: `corpus ${outcome}`, outcome, detail: null,
    }));
    importConfig.mockResolvedValueOnce(plan({ items }));
    const user = userEvent.setup();
    render(<ConfigUpload />);
    await upload(user, JSON.stringify(file));

    await user.click(screen.getByRole('button', { name: 'Check the file' }));
    const list = await screen.findByRole('list', { name: 'What the file does' });

    const tone = (outcome: string) => within(within(list).getByText(`corpus ${outcome}`).closest('li') as HTMLElement).getByText(outcome).className;
    expect(tone('create')).toMatch(/accent/);
    expect(tone('update')).toMatch(/accent/);
    expect(tone('unchanged')).toMatch(/bg-muted/);
    expect(tone('not imported')).toMatch(/bg-muted/);
    expect(tone('extra')).toMatch(/bg-muted/);
    expect(tone('refused')).toMatch(/warn/);
    expect(tone('failed')).toMatch(/danger/);
  });

  it('counts the items that change something and says it in the singular for one', async () => {
    importConfig.mockResolvedValueOnce(plan({
      counts: { create: 1, unchanged: 1 },
      items: [
        { kind: 'corpus', name: 'Papers', outcome: 'create', detail: null },
        { kind: 'corpus', name: 'Notes', outcome: 'unchanged', detail: null },
      ],
    }));
    const user = userEvent.setup();
    render(<ConfigUpload />);
    await upload(user, JSON.stringify(file));
    await user.click(screen.getByRole('button', { name: 'Check the file' }));
    await screen.findByText(/Checked, nothing changed/);

    await user.click(screen.getByRole('button', { name: 'Apply' }));

    expect(within(await screen.findByRole('dialog')).getByText(/1 item makes a change/)).toBeInTheDocument();
  });

  it('counts an update with the creates', async () => {
    importConfig.mockResolvedValueOnce(plan({
      counts: { update: 1 },
      items: [{ kind: 'source', name: 'Papers: workspace notes', outcome: 'update', detail: null }],
    }));
    const user = userEvent.setup();
    render(<ConfigUpload />);
    await upload(user, JSON.stringify(file));
    await user.click(screen.getByRole('button', { name: 'Check the file' }));
    await screen.findByText(/Checked, nothing changed/);

    await user.click(screen.getByRole('button', { name: 'Apply' }));

    expect(within(await screen.findByRole('dialog')).getByText(/1 item makes a change/)).toBeInTheDocument();
  });

  it('does not take a file that holds JSON null for a configuration file', async () => {
    const user = userEvent.setup();
    render(<ConfigUpload />);

    await upload(user, 'null');

    expect(await screen.findByText(/is not a JSON configuration file/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Check the file' })).toBeDisabled();
  });
});
