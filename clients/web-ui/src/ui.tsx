import { useEffect, useRef, useState, type ReactNode } from 'react';

// Small shared primitives. Deliberately hand-rolled rather than a component kit:
// "minimal but professional" is better served by a handful of pieces we control
// than by theming someone else's.

export function Badge({ tone = 'neutral', children }: { tone?: Tone; children: ReactNode }) {
  return (
    <span className="badge" style={toneStyle(tone)}>
      {children}
    </span>
  );
}

export type Tone = 'neutral' | 'ok' | 'warn' | 'danger' | 'accent';

function toneStyle(tone: Tone): React.CSSProperties {
  if (tone === 'neutral') return {};
  const c = `var(--${tone === 'accent' ? 'accent' : tone})`;
  return {
    color: c,
    borderColor: `color-mix(in oklab, ${c} 40%, transparent)`,
    background: `color-mix(in oklab, ${c} 12%, transparent)`,
  };
}

export function stateTone(state: string): Tone {
  switch (state) {
    case 'ready':
    case 'succeeded':
    case 'indexed':
      return 'ok';
    case 'indexing':
    case 'running':
    case 'queued':
      return 'accent';
    // `degraded` is its own colour, distinct from both running and failed. A job
    // that is technically alive but achieving nothing must not look like one that
    // is working.
    case 'degraded':
    case 'skipped':
    case 'empty':
      return 'warn';
    case 'failed':
    case 'unavailable':
    case 'cancelled':
      return 'danger';
    default:
      return 'neutral';
  }
}

export function Spinner() {
  return (
    <span
      aria-hidden
      style={{
        display: 'inline-block',
        width: '0.85em',
        height: '0.85em',
        border: '2px solid color-mix(in oklab, var(--text-dim) 35%, transparent)',
        borderTopColor: 'var(--accent)',
        borderRadius: '50%',
        animation: 'dexicon-spin 700ms linear infinite',
      }}
    />
  );
}

export function ErrorBanner({ error, onDismiss }: { error: unknown; onDismiss?: () => void }) {
  if (!error) return null;
  const message = error instanceof Error ? error.message : String(error);
  return (
    <div
      role="alert"
      className="card"
      style={{
        padding: '0.7rem 0.9rem',
        borderColor: 'color-mix(in oklab, var(--danger) 45%, transparent)',
        background: 'color-mix(in oklab, var(--danger) 8%, transparent)',
        display: 'flex',
        justifyContent: 'space-between',
        gap: '1rem',
        alignItems: 'start',
      }}
    >
      {/* The server's own message, verbatim. "Something went wrong" helps nobody. */}
      <span style={{ fontSize: '0.875rem' }}>{message}</span>
      {onDismiss && (
        <button className="btn" onClick={onDismiss} aria-label="Dismiss error">
          ✕
        </button>
      )}
    </div>
  );
}

export function Empty({ title, hint, action }: { title: string; hint?: string; action?: ReactNode }) {
  return (
    <div className="card" style={{ padding: '2.5rem 1.5rem', textAlign: 'center' }}>
      <p style={{ margin: 0, fontWeight: 600 }}>{title}</p>
      {/* Empty states say what to do next, never just "No data". */}
      {hint && <p className="dim" style={{ margin: '0.4rem 0 0', fontSize: '0.875rem' }}>{hint}</p>}
      {action && <div style={{ marginTop: '1rem' }}>{action}</div>}
    </div>
  );
}

export function Modal({
  title,
  onClose,
  children,
  width = 560,
}: {
  title: string;
  onClose: () => void;
  children: ReactNode;
  width?: number;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const restoreTo = useRef<Element | null>(null);

  useEffect(() => {
    restoreTo.current = document.activeElement;
    ref.current?.querySelector<HTMLElement>('input, button, select, textarea')?.focus();

    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
      if (e.key !== 'Tab' || !ref.current) return;

      // Trap focus inside the dialog, and restore it on close.
      const focusable = ref.current.querySelectorAll<HTMLElement>(
        'a[href], button:not(:disabled), input:not(:disabled), select, textarea, [tabindex]:not([tabindex="-1"])',
      );
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    };

    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
      (restoreTo.current as HTMLElement | null)?.focus?.();
    };
  }, [onClose]);

  return (
    <div
      onMouseDown={(e) => e.target === e.currentTarget && onClose()}
      style={{
        position: 'fixed',
        inset: 0,
        background: 'color-mix(in oklab, black 45%, transparent)',
        display: 'grid',
        placeItems: 'center',
        padding: '1rem',
        zIndex: 50,
      }}
    >
      <div
        ref={ref}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="card"
        style={{ width: '100%', maxWidth: width, maxHeight: '85vh', overflow: 'auto', padding: '1.1rem 1.25rem' }}
      >
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '0.9rem' }}>
          <h2 style={{ margin: 0, fontSize: '1rem', fontWeight: 650 }}>{title}</h2>
          <button className="btn" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label style={{ display: 'block', marginBottom: '0.85rem' }}>
      <span style={{ display: 'block', fontSize: '0.8rem', fontWeight: 600, marginBottom: '0.3rem' }}>{label}</span>
      {children}
      {hint && (
        <span className="dim" style={{ display: 'block', fontSize: '0.75rem', marginTop: '0.25rem' }}>
          {hint}
        </span>
      )}
    </label>
  );
}

export function CopyButton({ text, label = 'Copy' }: { text: string; label?: string }) {
  const [done, setDone] = useState(false);
  return (
    <button
      className="btn"
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(text);
          setDone(true);
          setTimeout(() => setDone(false), 1500);
        } catch {
          /* clipboard blocked; the text is on screen to select by hand */
        }
      }}
    >
      {done ? '✓ Copied' : label}
    </button>
  );
}

export function relativeTime(iso?: string): string {
  if (!iso) return 'never';
  const then = new Date(iso).getTime();
  const secs = Math.round((Date.now() - then) / 1000);
  if (secs < 60) return 'just now';
  if (secs < 3600) return `${Math.floor(secs / 60)}m ago`;
  if (secs < 86400) return `${Math.floor(secs / 3600)}h ago`;
  return `${Math.floor(secs / 86400)}d ago`;
}

export function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}
