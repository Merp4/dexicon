import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Badge, Button, Field, Input, Select, relativeTime, stateTone } from './ui';

/**
 * The form primitives, and the bug that caused them to exist.
 *
 * `.input` and `.btn-primary` used to be opt-in classes on bare elements. A whole modal
 * shipped with every field rendering with no border, no background and no padding, and
 * every primary button looking exactly like a secondary one. Nothing threw, the types
 * were fine, the build was clean and 163 server tests passed. It was found by someone
 * looking at a screenshot.
 *
 * These tests assert the CLASS is applied, which is the mechanism — jsdom computes no
 * layout, so it cannot tell you a field is invisible. Catching "actually invisible" needs
 * a real browser and is a slower, different kind of test. Catching "lost its styling
 * hook" is most of the value for none of the cost.
 */
describe('form primitives', () => {
  it('gives an input its styling without being asked', () => {
    render(<Input aria-label="Name" />);
    expect(screen.getByLabelText('Name')).toHaveClass('input');
  });

  it('keeps any class the caller adds as well', () => {
    render(<Input aria-label="Pattern" className="mono" />);
    const el = screen.getByLabelText('Pattern');
    expect(el).toHaveClass('input');
    expect(el).toHaveClass('mono');
  });

  it('gives a select the same styling as an input', () => {
    render(
      <Select aria-label="Provider">
        <option value="ollama">ollama</option>
      </Select>,
    );
    expect(screen.getByLabelText('Provider')).toHaveClass('input');
  });

  it('renders a primary button as btn-primary, not "btn primary"', () => {
    // The exact bug: `className="btn primary"` where the stylesheet defines `.btn-primary`.
    // It rendered as a plain secondary button — visually identical to Cancel.
    render(<Button variant="primary">Add set</Button>);
    const el = screen.getByRole('button', { name: 'Add set' });
    expect(el).toHaveClass('btn');
    expect(el).toHaveClass('btn-primary');
    // The class list, not a regex: `-` is a word boundary, so /\bprimary\b/ matches
    // inside `btn-primary` and the assertion would pass for the wrong reason.
    expect([...el.classList]).not.toContain('primary');
  });

  it('leaves a default button without a variant class', () => {
    render(<Button>Cancel</Button>);
    const el = screen.getByRole('button', { name: 'Cancel' });
    expect(el).toHaveClass('btn');
    expect(el).not.toHaveClass('btn-primary');
  });

  it('associates a Field label with the control inside it', () => {
    // A label that labels nothing is a screen-reader dead end.
    render(
      <Field label="Chunk size" hint="64–8192">
        <Input />
      </Field>,
    );
    expect(screen.getByLabelText(/Chunk size/)).toHaveClass('input');
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
