import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConfigDownload, fileName, fileText } from './ConfigDownload';

/**
 * The download of the configuration file. The file's content is the server's; what this screen adds is its name, its
 * indentation, and a line for each thing the server says it left out.
 */
const exportConfig = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: { exportConfig: (...a: unknown[]) => exportConfig(...a) },
}));

const created: Blob[] = [];
const downloads: { name: string; href: string }[] = [];

beforeEach(() => {
  exportConfig.mockReset();
  created.length = 0;
  downloads.length = 0;
  URL.createObjectURL = vi.fn((blob: Blob | MediaSource) => {
    created.push(blob as Blob);
    return 'blob:test';
  });
  URL.revokeObjectURL = vi.fn();
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
    downloads.push({ name: this.download, href: this.href });
  });
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('the file', () => {
  it('is named for the day it was made', () => {
    expect(fileName(new Date(2026, 10, 5, 23, 59))).toBe('dexicon-config-2026-11-05.json');
    expect(fileName(new Date(2027, 0, 31, 0, 0))).toBe('dexicon-config-2027-01-31.json');
  });

  it('is indented two spaces and ends with a line break', () => {
    expect(fileText({ a: { b: 1 } })).toBe('{\n  "a": {\n    "b": 1\n  }\n}\n');
  });
});

describe('the download', () => {
  it('asks the server once and hands the browser a JSON file named for today', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 10, 5, 10, 0));
    exportConfig.mockResolvedValue({ format: 'dexicon-config', version: 1, omitted: [] });
    const user = userEvent.setup({ advanceTimers: () => {} });
    render(<ConfigDownload />);

    await user.click(screen.getByRole('button', { name: 'Download configuration' }));

    await vi.waitFor(() => expect(downloads).toHaveLength(1));
    expect(exportConfig).toHaveBeenCalledTimes(1);
    expect(downloads[0]).toEqual({ name: 'dexicon-config-2026-11-05.json', href: 'blob:test' });
    expect(created[0].type).toBe('application/json');
    expect(await created[0].text()).toBe(fileText({ format: 'dexicon-config', version: 1, omitted: [] }));
    await vi.waitFor(() => expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:test'));
  });

  it('treats a file with no omitted list as one that left nothing out', async () => {
    exportConfig.mockResolvedValue({ format: 'dexicon-config', version: 1 });
    const user = userEvent.setup();
    render(<ConfigDownload />);

    await user.click(screen.getByRole('button', { name: 'Download configuration' }));

    await vi.waitFor(() => expect(downloads).toHaveLength(1));
    expect(screen.queryByText('Left out of the file')).not.toBeInTheDocument();
  });

  it('clears what the last file left out when the next download fails', async () => {
    exportConfig.mockResolvedValueOnce({ format: 'dexicon-config', version: 1, omitted: ['one thing left out'] });
    exportConfig.mockRejectedValueOnce(new Error('refused'));
    const user = userEvent.setup();
    render(<ConfigDownload />);

    await user.click(screen.getByRole('button', { name: 'Download configuration' }));
    expect(await screen.findByText('one thing left out')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Download configuration' }));

    expect(await screen.findByText(/refused/)).toBeInTheDocument();
    expect(screen.queryByText('one thing left out')).not.toBeInTheDocument();
  });

  it('clears an earlier error when the next download works', async () => {
    exportConfig.mockRejectedValueOnce(new Error('refused'));
    exportConfig.mockResolvedValueOnce({ format: 'dexicon-config', version: 1, omitted: [] });
    const user = userEvent.setup();
    render(<ConfigDownload />);

    await user.click(screen.getByRole('button', { name: 'Download configuration' }));
    expect(await screen.findByText(/refused/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Download configuration' }));

    await vi.waitFor(() => expect(downloads).toHaveLength(1));
    expect(screen.queryByText(/refused/)).not.toBeInTheDocument();
  });

  it('says what the file leaves out', async () => {
    exportConfig.mockResolvedValue({
      format: 'dexicon-config', version: 1,
      omitted: ["Corpus 'books' holds uploaded documents. Their content and their attachment are not in this file."],
    });
    const user = userEvent.setup();
    render(<ConfigDownload />);

    await user.click(screen.getByRole('button', { name: 'Download configuration' }));

    expect(await screen.findByText('Left out of the file')).toBeInTheDocument();
    expect(screen.getByText(/'books' holds uploaded documents/)).toBeInTheDocument();
  });

  it('shows no notice when nothing was left out', async () => {
    exportConfig.mockResolvedValue({ format: 'dexicon-config', version: 1, omitted: [] });
    const user = userEvent.setup();
    render(<ConfigDownload />);

    await user.click(screen.getByRole('button', { name: 'Download configuration' }));

    await vi.waitFor(() => expect(downloads).toHaveLength(1));
    expect(screen.queryByText('Left out of the file')).not.toBeInTheDocument();
  });

  it('shows the server\'s refusal and starts no download', async () => {
    exportConfig.mockRejectedValue(new Error('refused'));
    const user = userEvent.setup();
    render(<ConfigDownload />);

    await user.click(screen.getByRole('button', { name: 'Download configuration' }));

    expect(await screen.findByText(/refused/)).toBeInTheDocument();
    expect(downloads).toHaveLength(0);
    expect(screen.getByRole('button', { name: 'Download configuration' })).toBeEnabled();
  });

  it('disables the button while the file is being prepared', async () => {
    let release: (v: unknown) => void = () => {};
    exportConfig.mockReturnValue(new Promise((resolve) => { release = resolve; }));
    const user = userEvent.setup();
    render(<ConfigDownload />);

    await user.click(screen.getByRole('button', { name: 'Download configuration' }));

    expect(screen.getByRole('button', { name: 'Preparing…' })).toBeDisabled();
    release({ format: 'dexicon-config', version: 1, omitted: [] });
    await vi.waitFor(() => expect(downloads).toHaveLength(1));
  });
});
