import '@testing-library/jest-dom/vitest';
import { afterEach, vi } from 'vitest';
import { cleanup } from '@testing-library/react';

// Each test gets a clean document. Without this a modal from the previous test is still
// mounted and `getByLabelText` finds two of everything.
afterEach(cleanup);

/**
 * Gaps in jsdom, not in the app.
 *
 * The popover primitives measure and capture the pointer, and jsdom implements neither
 * the Pointer Events capture API nor ResizeObserver nor scrollIntoView. Without these a
 * select throws the moment it opens, which says nothing about whether the select works
 * in a browser. Everything here is a browser API jsdom simply does not have.
 */
Element.prototype.scrollIntoView ??= vi.fn();
Element.prototype.hasPointerCapture ??= vi.fn(() => false);
Element.prototype.setPointerCapture ??= vi.fn();
Element.prototype.releasePointerCapture ??= vi.fn();

globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
};

globalThis.DOMRect ??= class DOMRect {
  constructor(
    readonly x = 0,
    readonly y = 0,
    readonly width = 0,
    readonly height = 0,
  ) {}
  readonly top = 0;
  readonly left = 0;
  readonly right = 0;
  readonly bottom = 0;
  static fromRect() {
    return new DOMRect();
  }
  toJSON() {
    return {};
  }
} as unknown as typeof DOMRect;
