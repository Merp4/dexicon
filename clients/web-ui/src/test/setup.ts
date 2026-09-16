import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

// Each test gets a clean document. Without this a modal from the previous test is still
// mounted and `getByLabelText` finds two of everything.
afterEach(cleanup);
