import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import './index.css';

// Restore the theme before first paint so there is no flash of the wrong one.
try {
  const saved = localStorage.getItem('dexicon.theme');
  if (saved && saved !== 'auto') document.documentElement.dataset.theme = saved;
} catch {
  /* blocked storage: fall back to prefers-color-scheme */
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
