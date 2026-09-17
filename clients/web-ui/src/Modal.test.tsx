import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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
