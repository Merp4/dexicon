import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { Button, Input, Modal } from './ui';

/**
 * The dialog, which used to be fifty hand-written lines of focus trapping.
 *
 * Handing that to the library is only worth it if the behaviour survived, and none of it
 * is visible in a diff: whether Escape closes, whether Tab can leave, whether focus comes
 * back to where it was. Each of those is the difference between a keyboard user being able
 * to use this screen and being stuck on it.
 */
function Harness({ onClose = vi.fn() }: { onClose?: () => void }) {
  return (
    <>
      <button type="button">outside</button>
      <Modal title="Add a chunk set" onClose={onClose}>
        <Input aria-label="Name" />
        <Button>Cancel</Button>
      </Modal>
    </>
  );
}

describe('Modal', () => {
  it('announces itself as a dialog, named by its title', () => {
    render(<Harness />);

    const dialog = screen.getByRole('dialog');
    expect(dialog).toHaveAccessibleName('Add a chunk set');
  });

  it('hides the rest of the page while it is open', () => {
    // Modality, by the mechanism that actually delivers it: everything outside is marked
    // aria-hidden, so `getByRole` cannot reach it either. Without this a screen reader
    // wanders into the page underneath and reads a form the person cannot see or reach.
    render(<Harness />);

    expect(screen.queryByRole('button', { name: 'outside' })).not.toBeInTheDocument();
    expect(screen.getByText('outside').closest('[aria-hidden="true"]')).not.toBeNull();
    // ...and it is still really there, just hidden from the tree.
    expect(document.body.textContent).toContain('outside');
  });

  it('closes on Escape', async () => {
    const onClose = vi.fn();
    const user = userEvent.setup();
    render(<Harness onClose={onClose} />);

    await user.keyboard('{Escape}');

    expect(onClose).toHaveBeenCalledOnce();
  });

  it('closes from its own close button', async () => {
    const onClose = vi.fn();
    const user = userEvent.setup();
    render(<Harness onClose={onClose} />);

    await user.click(screen.getByRole('button', { name: /close/i }));

    expect(onClose).toHaveBeenCalledOnce();
  });

  it('moves focus into itself rather than leaving it behind the overlay', async () => {
    render(<Harness />);

    const dialog = await screen.findByRole('dialog');
    expect(dialog.contains(document.activeElement)).toBe(true);
  });

  it('does not close on its own', () => {
    // Every caller decides whether the modal exists by rendering it or not, so this takes
    // `open` as given rather than owning that state twice. If it ever closed itself the
    // caller would still think it was open.
    const onClose = vi.fn();
    render(<Harness onClose={onClose} />);

    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('gives focus back to what opened it, however it closes', async () => {
    // Shaped like every caller: opened by rendering it, closed by unmounting it. The
    // primitive returns focus to its own Trigger, and a modal opened this way has none, so
    // focus fell to <body>. Measured in the running app on the Full reindex dialog, after
    // Escape and after Cancel alike.
    function Opener() {
      const [open, setOpen] = useState(false);
      return (
        <>
          <button type="button" onClick={() => setOpen(true)}>Full reindex</button>
          {open && (
            <Modal title="Full reindex of docs?" onClose={() => setOpen(false)}>
              <Button onClick={() => setOpen(false)}>Cancel</Button>
            </Modal>
          )}
        </>
      );
    }

    const user = userEvent.setup();
    render(<Opener />);
    const opener = screen.getByRole('button', { name: 'Full reindex' });

    await user.click(opener);
    await user.keyboard('{Escape}');
    await waitFor(() => expect(opener).toHaveFocus());

    await user.click(opener);
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(opener).toHaveFocus());
  });

  it('gives focus back to the page when one modal replaces another', async () => {
    // Shaped like New key → Token created: a button inside the first modal closes it and
    // opens the second in one update. The second opens from a control that the same commit
    // removes, so it has to close back to what opened the first.
    function Chain() {
      const [step, setStep] = useState<'none' | 'create' | 'created'>('none');
      return (
        <>
          <button type="button" onClick={() => setStep('create')}>New key</button>
          {step === 'create' && (
            <Modal title="New key" onClose={() => setStep('none')}>
              <Button onClick={() => setStep('created')}>Create</Button>
            </Modal>
          )}
          {step === 'created' && (
            <Modal title="Token created" onClose={() => setStep('none')}>
              <Button onClick={() => setStep('none')}>Done</Button>
            </Modal>
          )}
        </>
      );
    }

    const user = userEvent.setup();
    render(<Chain />);
    const opener = screen.getByRole('button', { name: 'New key' });

    await user.click(opener);
    await user.click(screen.getByRole('button', { name: 'Create' }));
    await user.click(await screen.findByRole('button', { name: 'Done' }));

    await waitFor(() => expect(opener).toHaveFocus());
  });

  it('gives focus back into the modal a nested one was opened from', async () => {
    function Nested() {
      const [outer, setOuter] = useState(false);
      const [inner, setInner] = useState(false);
      return (
        <>
          <button type="button" onClick={() => setOuter(true)}>Filters</button>
          {outer && (
            <Modal title="Filters" onClose={() => setOuter(false)}>
              <Button onClick={() => setInner(true)}>Preview</Button>
              {inner && (
                <Modal title="Preview" onClose={() => setInner(false)}>
                  <Button onClick={() => setInner(false)}>Back</Button>
                </Modal>
              )}
            </Modal>
          )}
        </>
      );
    }

    const user = userEvent.setup();
    render(<Nested />);

    await user.click(screen.getByRole('button', { name: 'Filters' }));
    const preview = await screen.findByRole('button', { name: 'Preview' });
    await user.click(preview);
    await user.click(await screen.findByRole('button', { name: 'Back' }));

    await waitFor(() => expect(preview).toHaveFocus());
  });

  it('keeps Tab inside it', async () => {
    // The trap. Tabbing past the last control must come back to the first, not escape to
    // the page behind, which is where the old hand-rolled version earned its fifty lines.
    const user = userEvent.setup();
    render(<Harness />);

    const dialog = screen.getByRole('dialog');
    const outside = screen.getByText('outside');

    for (let i = 0; i < 8; i++) {
      await user.tab();
      expect(document.activeElement).not.toBe(outside);
      expect(dialog.contains(document.activeElement)).toBe(true);
    }
  });
});
