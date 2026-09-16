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

const style = document.createElement('style');
style.textContent = '@keyframes dexicon-spin { to { transform: rotate(360deg) } }';
document.head.appendChild(style);

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
