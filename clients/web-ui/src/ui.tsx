import { createContext, useContext, useId, useState, type ReactNode } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from 'cn';
import { Check, Copy, Loader2, X } from 'lucide-react';

import { Button as ShadButton } from '@/components/ui/button';
import { Input as ShadInput } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import {
  Select as SelectRoot,
  SelectContent,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';

/**
 * The app's vocabulary, spoken in shadcn components.
 *
 * `src/components/ui/*` is shadcn's own code, left exactly as its CLI writes it so that
 * `shadcn add` and `shadcn diff` keep working. This file is the layer between that and
 * Dexicon: it keeps the names the app already uses — `Modal`, `Field`, a `Badge` with a
 * tone, a `Button` whose confirming variant is called `primary` — so adopting a component
 * library did not mean rewriting every call site into someone else's nouns.
 *
 * It also keeps the property these primitives were written for: a component you cannot
 * construct WITHOUT its styling. `.input` and `.btn-primary` used to be opt-in classes on
 * bare elements, and a whole modal shipped with every field invisible and every primary
 * button rendering as a secondary one. Nothing failed; the screen was simply wrong.
 */

// ── Buttons ─────────────────────────────────────────────────────────────────

/**
 * Dexicon has three kinds of button; shadcn has six.
 *
 * The app's unnamed default is a secondary action, which is shadcn's `outline`, and
 * `primary` is the one confirming action on a screen, which is shadcn's `default`.
 * Mapping here rather than renaming every call site keeps `primary` meaning what it
 * means to us.
 */
const variantMap = {
  default: 'outline',
  primary: 'default',
  danger: 'destructive',
  ghost: 'ghost',
  secondary: 'secondary',
} as const;

export function Button({
  variant = 'default',
  size = 'sm',
  ...rest
}: Omit<React.ComponentProps<typeof ShadButton>, 'variant'> & {
  variant?: keyof typeof variantMap;
}) {
  return <ShadButton variant={variantMap[variant]} size={size} {...rest} />;
}

/**
 * One of a row of choices, the current one wearing the accent.
 *
 * `active` is only how it LOOKS. How it is announced depends on what kind of choice the
 * caller says it is: a radio group says `aria-checked`, navigation says `aria-current`,
 * and only a genuine toggle says `aria-pressed`. Forcing `aria-pressed` on all three put
 * invalid ARIA on the radios and made a screen reader call every nav item a toggle
 * button, so it is set only when the caller has claimed neither of the others.
 *
 * Note the `dark:` repeats below. shadcn's outline variant carries `dark:bg-input/30` and
 * `dark:border-input`, and a dark-variant utility is emitted after the plain ones — so a
 * plain `bg-*` here loses to it in dark mode and the override silently does nothing.
 * Anything overriding this component's background or border needs its dark twin.
 */
export function Chip({
  active = false,
  flat = false,
  className,
  role,
  'aria-current': current,
  ...rest
}: React.ComponentProps<typeof Button> & { active?: boolean; flat?: boolean }) {
  const isToggle = role === undefined && current === undefined;

  return (
    <Button
      role={role}
      aria-current={current}
      // Defaulted rather than left off: an unpressed toggle that omits `aria-pressed`
      // does not read as a choice at all, so the group stops being a group.
      aria-pressed={isToggle ? active : undefined}
      className={cn(
        active
          ? 'border-[color-mix(in_oklab,var(--accent)_35%,transparent)] bg-[var(--accent-soft)] text-[var(--accent)] dark:bg-[var(--accent-soft)]'
          : flat && 'border-transparent bg-transparent dark:border-transparent dark:bg-transparent',
        className,
      )}
      {...rest}
    />
  );
}

/**
 * A whole card that is one click target.
 *
 * A button, not a div with an onClick: it is reachable by Tab, activates on Enter and
 * Space, and announces itself as something that can be pressed. The last bare element in
 * the app was this one, styled by an opt-in class — which is the pattern that shipped a
 * modal with every field invisible.
 */
export function CardButton({ className, ...rest }: React.ComponentProps<'button'>) {
  return (
    <button
      type="button"
      className={cn(
        'w-full cursor-pointer rounded-lg border border-border bg-card p-3.5 text-left',
        'transition-colors hover:border-[color-mix(in_oklab,var(--accent)_35%,transparent)] hover:bg-muted',
        'focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none',
        className,
      )}
      {...rest}
    />
  );
}

// ── Fields ──────────────────────────────────────────────────────────────────

/**
 * A field's generated ids, so that its label and hint reach the control.
 *
 * The label used to WRAP the control, which associates the two but also folds the hint
 * into the label: a screen reader announced "Chunk size (tokens) 64–8192. Roughly four
 * characters each." as the field's NAME. An explicit `htmlFor` plus `aria-describedby`
 * says the label as the name and the hint as the description, which is what each is.
 */
const FieldContext = createContext<{ id: string; hintId?: string } | null>(null);

/** The id and description a control adopts when it sits inside a Field. */
function useField(id?: string) {
  const field = useContext(FieldContext);
  return { id: id ?? field?.id, 'aria-describedby': field?.hintId };
}

export function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: ReactNode;
}) {
  const id = useId();
  const hintId = `${id}-hint`;

  return (
    <FieldContext.Provider value={{ id, hintId: hint ? hintId : undefined }}>
      <div className="mb-3.5 grid gap-1.5">
        <Label htmlFor={id} className="text-xs font-semibold">
          {label}
        </Label>
        {children}
        {hint && (
          <p id={hintId} className="text-xs text-muted-foreground">
            {hint}
          </p>
        )}
      </div>
    </FieldContext.Provider>
  );
}

export function Input({ id, ...rest }: React.ComponentProps<typeof ShadInput>) {
  return <ShadInput {...useField(id)} {...rest} />;
}

/**
 * A select in a popover of our own rather than the operating system's.
 *
 * The native control paints a menu the page has no say over: it ignored the app's dark
 * mode and drew over whatever sat beside it. This one is an ordinary portalled element,
 * so it is themed, scrollable and positioned like everything else on the screen.
 */
export function Select({
  value,
  onValueChange,
  placeholder,
  disabled,
  className,
  children,
  id,
  'aria-label': ariaLabel,
}: {
  value: string;
  onValueChange: (value: string) => void;
  placeholder?: string;
  disabled?: boolean;
  className?: string;
  children: ReactNode;
  id?: string;
  'aria-label'?: string;
}) {
  return (
    <SelectRoot value={value} onValueChange={onValueChange} disabled={disabled}>
      <SelectTrigger {...useField(id)} aria-label={ariaLabel} className={cn('w-full', className)}>
        <SelectValue placeholder={placeholder} />
      </SelectTrigger>
      <SelectContent>{children}</SelectContent>
    </SelectRoot>
  );
}

export { SelectItem } from '@/components/ui/select';

// Re-exported rather than imported straight from the library by call sites: this file is
// the one place the app's vocabulary is defined, and a screen reaching past it is how two
// import paths for the same control start.
export { Checkbox } from '@/components/ui/checkbox';

// ── Badges ──────────────────────────────────────────────────────────────────

export type Tone = 'neutral' | 'ok' | 'warn' | 'danger' | 'accent';

/**
 * Tones are the app's five meanings, not shadcn's four looks.
 *
 * `color-mix` rather than a fixed pair per tone: one token drives the text, the border
 * and the fill together, so a palette change cannot leave a badge half-updated.
 */
const badgeVariants = cva(
  'inline-flex items-center gap-1 whitespace-nowrap rounded-full border px-1.5 py-0.5 text-[0.72rem] font-semibold [&_svg]:size-3 [&_svg]:shrink-0',
  {
    variants: {
      tone: {
        neutral: 'border-border bg-muted text-muted-foreground',
        ok: 'border-[color-mix(in_oklab,var(--ok)_40%,transparent)] bg-[color-mix(in_oklab,var(--ok)_12%,transparent)] text-[var(--ok)]',
        warn: 'border-[color-mix(in_oklab,var(--warn)_40%,transparent)] bg-[color-mix(in_oklab,var(--warn)_12%,transparent)] text-[var(--warn)]',
        danger:
          'border-[color-mix(in_oklab,var(--danger)_40%,transparent)] bg-[color-mix(in_oklab,var(--danger)_12%,transparent)] text-[var(--danger)]',
        accent:
          'border-[color-mix(in_oklab,var(--accent)_40%,transparent)] bg-[color-mix(in_oklab,var(--accent)_12%,transparent)] text-[var(--accent)]',
      },
    },
    defaultVariants: { tone: 'neutral' },
  },
);

export function Badge({
  tone,
  className,
  children,
}: VariantProps<typeof badgeVariants> & { className?: string; children: ReactNode }) {
  return <span className={cn(badgeVariants({ tone }), className)}>{children}</span>;
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
    // Attached but not yet chunked. Shown as work outstanding, never as done —
    // "indexed · 0 chunks" was the old lie.
    case 'pending':
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

// ── Feedback ────────────────────────────────────────────────────────────────

export function Spinner({ className }: { className?: string }) {
  return <Loader2 aria-hidden className={cn('size-3.5 animate-spin', className)} />;
}

export function ErrorBanner({ error, onDismiss }: { error: unknown; onDismiss?: () => void }) {
  if (!error) return null;
  const message = error instanceof Error ? error.message : String(error);

  return (
    <div
      role="alert"
      className="mb-3 flex items-start justify-between gap-4 rounded-lg border border-[color-mix(in_oklab,var(--danger)_45%,transparent)] bg-[color-mix(in_oklab,var(--danger)_8%,transparent)] px-3.5 py-2.5"
    >
      {/* The server's own message, verbatim. "Something went wrong" helps nobody. */}
      <span className="text-sm">{message}</span>
      {onDismiss && (
        <Button variant="ghost" size="icon-xs" aria-label="Dismiss error" onClick={onDismiss}>
          <X />
        </Button>
      )}
    </div>
  );
}

export function Empty({
  title,
  hint,
  action,
}: {
  title: string;
  hint?: string;
  action?: ReactNode;
}) {
  return (
    <div className="rounded-lg border border-border bg-card px-6 py-10 text-center">
      <p className="m-0 font-semibold">{title}</p>
      {/* Empty states say what to do next, never just "No data". */}
      {hint && <p className="mt-1.5 mb-0 text-sm text-muted-foreground">{hint}</p>}
      {action && <div className="mt-4">{action}</div>}
    </div>
  );
}

// ── Modal ───────────────────────────────────────────────────────────────────

/**
 * An always-open dialog that reports its close.
 *
 * Every caller already decides whether the modal exists by rendering it or not, so this
 * takes `open` as given rather than owning that state twice. The focus trap, the Escape
 * key, the overlay and restoring focus afterwards were fifty hand-written lines here and
 * are now the dialog primitive's problem.
 */
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
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        className="max-h-[85vh] overflow-y-auto"
        style={{ maxWidth: `min(calc(100% - 2rem), ${width}px)` }}
        // Not every modal has a summary line, and a described-by pointing at nothing is
        // worse than none at all.
        aria-describedby={undefined}
      >
        <DialogHeader>
          <DialogTitle className="text-base">{title}</DialogTitle>
        </DialogHeader>
        {children}
      </DialogContent>
    </Dialog>
  );
}

// ── Odds and ends ───────────────────────────────────────────────────────────

export function CopyButton({ text, label = 'Copy' }: { text: string; label?: string }) {
  const [done, setDone] = useState(false);

  return (
    <Button
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
      {done ? <Check /> : <Copy />}
      {done ? 'Copied' : label}
    </Button>
  );
}

/**
 * The ONLY place a local time exists.
 *
 * Dexicon stores, returns and logs UTC. The browser is the single edge that converts,
 * because it is the only component that knows whose clock to use.
 *
 * `parseUtc` exists because the API used to serialise DateTime without a `Z` — SQLite
 * has no date type, so EF read values back with Kind=Unspecified — and
 * `new Date("2026-09-16T17:08:11")` parses THAT as local time. Every relative time was
 * silently wrong by the viewer's UTC offset. The server is fixed, and this stays as a
 * belt-and-braces parse: an ISO string with no zone is treated as UTC, never as local.
 */
export function parseUtc(iso: string): Date {
  const hasZone = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(iso);
  return new Date(hasZone ? iso : `${iso}Z`);
}

export function relativeTime(iso?: string | null): string {
  if (!iso) return 'never';
  const secs = Math.round((Date.now() - parseUtc(iso).getTime()) / 1000);
  if (secs < 0) return 'just now';        // small clock skew, not the future
  if (secs < 60) return 'just now';
  if (secs < 3600) return `${Math.floor(secs / 60)}m ago`;
  if (secs < 86400) return `${Math.floor(secs / 3600)}h ago`;
  return `${Math.floor(secs / 86400)}d ago`;
}

/** Absolute time in the viewer's own locale and zone — for titles and tooltips. */
export function localTime(iso?: string | null): string {
  if (!iso) return 'never';
  return parseUtc(iso).toLocaleString(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  });
}

export function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}
