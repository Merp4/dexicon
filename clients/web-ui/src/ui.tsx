import { createContext, type ReactNode, useContext, useId, useRef, useState } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from 'cn';
import { Check, CircleAlert, CircleCheck, Copy, Info, Loader2, TriangleAlert, X } from 'lucide-react';

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
 * Dexicon: it keeps the names the app already uses (`Modal`, `Field`, a `Badge` with a
 * tone, a `Button` whose confirming variant is called `primary`) so adopting a component
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
 * `dark:border-input`, and a dark-variant utility is emitted after the plain ones, so a
 * plain `bg-*` here loses to it in dark mode and the override has no effect.
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
 * One-of-N, as a single control rather than a row of loose buttons.
 *
 * Three separately-bordered pills with a hairline between them read as three unrelated
 * buttons that happen to be adjacent: the reader has to work out that they are alternatives,
 * and at small sizes the gap looks like a rendering fault. A segmented control says
 * "pick one of these" in its shape: one border around the set, one raised item inside it.
 *
 * It is also the accessible answer. A radio group is ONE tab stop with arrow keys moving
 * between the options; a row of buttons is N tab stops and no arrow keys. That is the
 * roving tabindex below, and it is the reason this is a component rather than a class
 * name: the behaviour has to travel with the appearance or it gets left out.
 */
export function Segmented<T extends string>({
  value,
  onChange,
  options,
  label,
  className,
}: {
  value: T;
  onChange: (value: T) => void;
  options: readonly { value: T; label: React.ReactNode; title?: string }[];
  /** Announced as the group's name, so the choice has a subject. */
  label: string;
  className?: string;
}) {
  const refs = useRef<(HTMLButtonElement | null)[]>([]);

  function move(from: number, delta: number) {
    const next = (from + delta + options.length) % options.length;
    onChange(options[next].value);
    refs.current[next]?.focus();
  }

  return (
    <div
      role="radiogroup"
      aria-label={label}
      className={cn(
        // h-9 matches Input and Select. The pills were 30px against their 36px, which is
        // what made the row look squashed: three short controls sitting inside a line of
        // taller ones, with the baseline wandering between them.
        'inline-flex h-9 items-center gap-0.5 rounded-md border border-input bg-muted/50 p-1',
        'dark:bg-input/30',
        className,
      )}
    >
      {options.map((option, i) => {
        const selected = option.value === value;
        return (
          <button
            key={option.value}
            ref={(el) => {
              refs.current[i] = el;
            }}
            type="button"
            role="radio"
            aria-checked={selected}
            title={option.title}
            // Roving: only the selected option is in the tab order, so the group is one
            // stop and the arrow keys do the rest.
            tabIndex={selected ? 0 : -1}
            onClick={() => onChange(option.value)}
            onKeyDown={(e) => {
              if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
                e.preventDefault();
                move(i, 1);
              } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
                e.preventDefault();
                move(i, -1);
              }
            }}
            className={cn(
              'flex h-full items-center rounded-[0.3rem] px-2.5 text-xs font-medium whitespace-nowrap',
              'transition-colors outline-none',
              'focus-visible:ring-[3px] focus-visible:ring-ring/50',
              selected
                ? 'bg-[var(--accent-soft)] text-[var(--accent)] shadow-xs'
                : 'text-muted-foreground hover:text-foreground',
            )}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}

/**
 * A whole card that is one click target.
 *
 * A button, not a div with an onClick: it is reachable by Tab, activates on Enter and
 * Space, and announces itself as something that can be pressed. The last bare element in
 * the app was this one, styled by an opt-in class, which is the pattern that shipped a
 * modal with every field invisible.
 */
export function CardButton({ className, ...rest }: React.ComponentProps<'button'>) {
  return (
    <button
      type="button"
      className={cn(
        'w-full rounded-lg border border-border bg-card p-3.5 text-left',
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
        ok: 'border-[color-mix(in_oklab,var(--ok)_40%,transparent)] bg-[color-mix(in_oklab,var(--ok)_12%,transparent)] text-[var(--ok-text)]',
        warn: 'border-[color-mix(in_oklab,var(--warn)_40%,transparent)] bg-[color-mix(in_oklab,var(--warn)_12%,transparent)] text-[var(--warn-text)]',
        danger:
          'border-[color-mix(in_oklab,var(--danger)_40%,transparent)] bg-[color-mix(in_oklab,var(--danger)_12%,transparent)] text-[var(--danger-text)]',
        accent:
          'border-[color-mix(in_oklab,var(--accent)_40%,transparent)] bg-[color-mix(in_oklab,var(--accent)_12%,transparent)] text-[var(--accent-text)]',
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
    // Attached but not yet chunked. Shown as work outstanding rather than as done;
    // "indexed · 0 chunks" was the previous, incorrect display.
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
      className="mb-3 flex items-start justify-between gap-4 rounded-lg border border-[color-mix(in_oklab,var(--danger)_45%,transparent)] bg-[color-mix(in_oklab,var(--danger)_8%,transparent)] px-3.5 py-2.5 animate-in fade-in-0 slide-in-from-top-1 duration-200"
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

/**
 * Something the screen has to say that is not an error.
 *
 * Nine of these were written by hand: `text-[var(--warn)]` on a paragraph in one place, a
 * card carrying a warn border in another, a `⚠` typed into the sentence in four more. No
 * two matched, and one of them was the wrong colour outright — a search running against a
 * half-built index was drawn in the accent blue, the same blue that badges the default
 * chunk set, so the one line on screen saying the results were incomplete read as a label.
 *
 * The tones are Badge's five, so a warning is the same yellow wherever it appears, and the
 * icon follows from the tone rather than from a character typed into the string.
 *
 * `role="status"` by default: these appear in response to something the reader just did —
 * running a search, typing a chunk size — and a message that only exists visually does not
 * reach anyone driving by keyboard and screen reader. Polite rather than `alert`, which
 * interrupts; ErrorBanner keeps `alert`, because a failed request should.
 */
const noticeVariants = cva(
  'flex items-start gap-2.5 rounded-lg border px-3.5 py-2.5 text-sm [&>svg]:mt-px [&>svg]:size-4 [&>svg]:shrink-0 animate-in fade-in-0 slide-in-from-top-1 duration-200',
  {
    variants: {
      tone: {
        neutral: 'border-border bg-muted text-muted-foreground',
        ok: 'border-[color-mix(in_oklab,var(--ok)_45%,transparent)] bg-[color-mix(in_oklab,var(--ok)_8%,transparent)]',
        warn: 'border-[color-mix(in_oklab,var(--warn)_45%,transparent)] bg-[color-mix(in_oklab,var(--warn)_8%,transparent)]',
        danger:
          'border-[color-mix(in_oklab,var(--danger)_45%,transparent)] bg-[color-mix(in_oklab,var(--danger)_8%,transparent)]',
        accent:
          'border-[color-mix(in_oklab,var(--accent)_45%,transparent)] bg-[color-mix(in_oklab,var(--accent)_8%,transparent)]',
      },
    },
    defaultVariants: { tone: 'warn' },
  },
);

/** The icon a tone means, so no call site picks one. */
const noticeIcons = {
  neutral: Info,
  accent: Info,
  ok: CircleCheck,
  warn: TriangleAlert,
  danger: CircleAlert,
} as const;

/** Only the glyph is tinted; the body keeps the page's own text colour. The glyph still
 *  has to be legible, so it takes the text-weight token rather than the fill one. */
const noticeIconTone = {
  neutral: 'text-muted-foreground',
  accent: 'text-[var(--accent-text)]',
  ok: 'text-[var(--ok-text)]',
  warn: 'text-[var(--warn-text)]',
  danger: 'text-[var(--danger-text)]',
} as const;

export function Notice({
  tone = 'warn',
  className,
  children,
  role = 'status',
}: {
  tone?: Tone;
  className?: string;
  children: ReactNode;
  role?: 'status' | 'alert' | 'none';
}) {
  const Icon = noticeIcons[tone];

  return (
    <div
      role={role === 'none' ? undefined : role}
      className={cn(noticeVariants({ tone }), className)}
    >
      <Icon aria-hidden className={noticeIconTone[tone]} />
      <div className="min-w-0 flex-1">{children}</div>
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
 * `parseUtc` exists because the API used to serialise DateTime without a `Z`. SQLite
 * has no date type, so EF read values back with Kind=Unspecified, and
 * `new Date("2026-09-16T17:08:11")` parses that as local time. Every relative time was
 * wrong by the viewer's UTC offset, with nothing reporting it. The server is fixed, and this stays as a
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
