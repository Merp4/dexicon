import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { Segmented } from './ui';

/**
 * One-of-N choices.
 *
 * Three separately-bordered pills read as three unrelated buttons that happen to be
 * adjacent, and behave that way too: N tab stops, no arrow keys. A radio group is ONE tab
 * stop with the arrows moving between options, which is the part most likely to be
 * dropped by anyone restyling this later, so it is the part with tests.
 */
function Harness({ onChange = vi.fn() }: { onChange?: (v: string) => void }) {
  const [value, setValue] = useState('hybrid');
  return (
    <>
      <button type="button">before</button>
      <Segmented
        label="Search mode"
        value={value}
        onChange={(v) => {
          setValue(v);
          onChange(v);
        }}
        options={[
          { value: 'hybrid', label: 'hybrid' },
          { value: 'semantic', label: 'semantic' },
          { value: 'keyword', label: 'keyword' },
        ]}
      />
      <button type="button">after</button>
    </>
  );
}

describe('a one-of-N choice', () => {
  it('is announced as a group with a name', async () => {
    render(<Harness />);

    const group = screen.getByRole('radiogroup', { name: 'Search mode' });
    expect(within(group).getAllByRole('radio')).toHaveLength(3);
  });

  it('marks exactly one option as chosen', async () => {
    render(<Harness />);

    const chosen = screen.getAllByRole('radio').filter((r) => r.getAttribute('aria-checked') === 'true');
    expect(chosen).toHaveLength(1);
    expect(chosen[0]).toHaveTextContent('hybrid');
  });

  it('is a single tab stop, not three', async () => {
    // The whole reason this is a radio group. With three tab stops, reaching the control
    // after it means pressing Tab three times for a choice already made.
    const user = userEvent.setup();
    render(<Harness />);

    await user.click(screen.getByRole('button', { name: 'before' }));
    await user.tab();
    expect(screen.getByRole('radio', { name: 'hybrid' })).toHaveFocus();

    await user.tab();
    expect(screen.getByRole('button', { name: 'after' })).toHaveFocus();
  });

  it('moves between options with the arrow keys', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    render(<Harness onChange={onChange} />);

    await user.click(screen.getByRole('radio', { name: 'hybrid' }));
    await user.keyboard('{ArrowRight}');

    expect(onChange).toHaveBeenLastCalledWith('semantic');
    expect(screen.getByRole('radio', { name: 'semantic' })).toHaveFocus();
  });

  it('wraps around at both ends', async () => {
    // Arrowing left from the first option should land on the last, not stop dead.
    const onChange = vi.fn();
    const user = userEvent.setup();
    render(<Harness onChange={onChange} />);

    await user.click(screen.getByRole('radio', { name: 'hybrid' }));
    await user.keyboard('{ArrowLeft}');

    expect(onChange).toHaveBeenLastCalledWith('keyword');
  });

  it('still chooses on a click', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    render(<Harness onChange={onChange} />);

    await user.click(screen.getByRole('radio', { name: 'keyword' }));

    expect(onChange).toHaveBeenCalledWith('keyword');
    expect(screen.getByRole('radio', { name: 'keyword' })).toHaveAttribute('aria-checked', 'true');
  });

  it('does not submit the form it sits in', async () => {
    // These live inside the search form. A button with no explicit type submits it, so
    // picking a mode would fire the search before the mode had been applied.
    render(<Harness />);

    for (const option of screen.getAllByRole('radio')) {
      expect(option).toHaveAttribute('type', 'button');
    }
  });
});
