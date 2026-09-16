import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Badge, Button, Chip, Field, Input, Select, SelectItem, relativeTime, stateTone } from './ui';

/**
 * The primitives, and the bug that caused them to exist.
 *
 * `.input` and `.btn-primary` used to be opt-in classes on bare `<input>` and `<button>`
 * elements. A whole modal shipped with every field rendering with no border, no background
 * and no padding, and every primary button looking exactly like a secondary one. Nothing
 * threw, the types were fine, the build was clean and 163 server tests passed. It was
 * found by someone looking at a screenshot.
 *
 * So the assertions below are about IDENTITY, not appearance: `data-slot` and
 * `data-variant` are the component library's own markers, so they say "this is the
 * component, wearing the variant asked for" — which is exactly what went wrong — without
 * pinning the test to a class list that is free to change. jsdom computes no layout and
 * could not tell you a field was invisible even if the test tried.
 */
describe('form primitives', () => {
  it('renders a real input, not a bare one', () => {
    render(<Input aria-label="Name" />);
    expect(screen.getByLabelText('Name')).toHaveAttribute('data-slot', 'input');
  });

  it('keeps any class the caller adds', () => {
    render(<Input aria-label="Pattern" className="font-mono" />);
    expect(screen.getByLabelText('Pattern')).toHaveClass('font-mono');
  });

  it('renders a select as a real control with its own popover', () => {
    // Native `<select>` painted a menu the page had no say over: it ignored dark mode
    // and drew over whatever sat beside it.
    render(
      <Select aria-label="Provider" value="ollama" onValueChange={() => {}}>
        <SelectItem value="ollama">ollama</SelectItem>
      </Select>,
    );
    const trigger = screen.getByLabelText('Provider');
    expect(trigger).toHaveAttribute('data-slot', 'select-trigger');
    expect(trigger).toHaveRole('combobox');
  });

  it('shows the chosen value on the closed select', () => {
    render(
      <Select aria-label="Mode" value="blank-line" onValueChange={() => {}}>
        <SelectItem value="language-aware">language-aware</SelectItem>
        <SelectItem value="blank-line">blank-line</SelectItem>
      </Select>,
    );
    expect(screen.getByLabelText('Mode')).toHaveTextContent('blank-line');
  });

  it('gives a primary button the confirming variant, not a stray class', () => {
    // The exact bug: `className="btn primary"` where the stylesheet defined
    // `.btn-primary`. It rendered as a plain secondary button, identical to Cancel.
    render(<Button variant="primary">Add set</Button>);
    expect(screen.getByRole('button', { name: 'Add set' })).toHaveAttribute(
      'data-variant',
      'default',
    );
  });

  it('gives an unmarked button the secondary variant', () => {
    render(<Button>Cancel</Button>);
    expect(screen.getByRole('button', { name: 'Cancel' })).toHaveAttribute(
      'data-variant',
      'outline',
    );
  });

  it('distinguishes the two, which is the whole point', () => {
    render(
      <>
        <Button variant="primary">Save</Button>
        <Button>Cancel</Button>
      </>,
    );
    expect(screen.getByRole('button', { name: 'Save' }).dataset.variant).not.toBe(
      screen.getByRole('button', { name: 'Cancel' }).dataset.variant,
    );
  });

  it('marks a destructive button as destructive', () => {
    render(<Button variant="danger">Delete permanently</Button>);
    expect(screen.getByRole('button', { name: 'Delete permanently' })).toHaveAttribute(
      'data-variant',
      'destructive',
    );
  });
});

describe('Chip', () => {
  it('reports which choice is the current one', () => {
    // Four screens grew their own inline version of "this button is the selected one",
    // and none of them told a screen reader about it.
    render(
      <>
        <Chip active>dark</Chip>
        <Chip>light</Chip>
      </>,
    );
    expect(screen.getByRole('button', { name: 'dark' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'light' })).toHaveAttribute('aria-pressed', 'false');
  });
});

describe('Field', () => {
  it('names the control by its label', () => {
    render(
      <Field label="Chunk size" hint="64–8192. Roughly four characters each.">
        <Input />
      </Field>,
    );
    // The label used to WRAP the control, which folded the hint into the accessible name:
    // "Chunk size 64–8192. Roughly four characters each." was announced as the NAME.
    expect(screen.getByLabelText('Chunk size')).toHaveAttribute('data-slot', 'input');
  });

  it('offers the hint as a description rather than part of the name', () => {
    render(
      <Field label="Overlap" hint="Must be smaller than the chunk size.">
        <Input />
      </Field>,
    );
    expect(screen.getByLabelText('Overlap')).toHaveAccessibleDescription(
      'Must be smaller than the chunk size.',
    );
  });

  it('labels a select the same way it labels an input', () => {
    render(
      <Field label="Boundary mode">
        <Select value="none" onValueChange={() => {}}>
          <SelectItem value="none">none</SelectItem>
        </Select>
      </Field>,
    );
    expect(screen.getByLabelText('Boundary mode')).toHaveAttribute(
      'data-slot',
      'select-trigger',
    );
  });
});

describe('state tone', () => {
  it('does not colour an unknown state as an error', () => {
    // A state this build has not heard of is neutral, not alarming. Inventing a red
    // badge for it would report a fault that may not exist.
    expect(stateTone('some-future-state')).toBe('neutral');
  });

  it.each([
    ['ready', 'ok'],
    ['indexing', 'accent'],
    ['pending', 'accent'],
    ['degraded', 'warn'],
    ['failed', 'danger'],
  ])('maps %s to %s', (state, tone) => {
    expect(stateTone(state)).toBe(tone);
  });
});

describe('relativeTime', () => {
  it('accepts the null a nullable C# timestamp serialises to', () => {
    // The generated types say `string | null`; the UI used to say `string | undefined`,
    // and every corpus that had never been indexed hit it.
    expect(relativeTime(null)).toBe('never');
    expect(relativeTime(undefined)).toBe('never');
  });

  it('reads a zone-less timestamp as UTC rather than local', () => {
    // SQLite has no date type, so timestamps once came back without a `Z` and
    // `new Date(...)` parsed them as local — every relative time silently wrong by the
    // viewer's offset.
    const justNow = new Date(Date.now() - 5_000).toISOString().replace('Z', '');
    expect(relativeTime(justNow)).toBe('just now');
  });

  it('does not report a small clock skew as the future', () => {
    const slightlyAhead = new Date(Date.now() + 2_000).toISOString();
    expect(relativeTime(slightlyAhead)).toBe('just now');
  });
});

describe('Badge', () => {
  it('renders its text', () => {
    render(<Badge tone="warn">3 pending</Badge>);
    expect(screen.getByText('3 pending')).toBeInTheDocument();
  });
});
