/**
 * README screenshots, taken against the real running instance.
 *
 *   cd clients/web-ui && npm install --no-save playwright && npx playwright install chromium
 *   node scripts/screenshot.mjs
 *
 * Playwright is deliberately NOT a dependency of this project. It is needed to take a
 * picture, not to build or test anything, and adding a browser download to every
 * `npm install` for that would be a poor trade. `--no-save` keeps it out of package.json
 * and out of the dependency tree that docs/10 reviews.
 *
 * THE TOKEN IS READ IN THIS PROCESS AND NEVER PRINTED. Same reasoning as
 * scripts/dev-token.py: whatever drives the browser should not put a live credential into
 * a transcript, a log or a shell history. It goes from .env into the page's sessionStorage
 * through an init script and nowhere else.
 *
 * Shots are written to docs/images/ and are committed — a README that renders a broken
 * image is worse than one with no image at all.
 */

import { existsSync, mkdirSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const REPO = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const BASE = process.env.DEXICON_BASE ?? 'http://127.0.0.1:8477';
const OUT = join(REPO, 'docs', 'images');

/** Environment first, then .env — the precedence the other tooling uses. */
function token() {
  if (process.env.DEXICON_TOKEN) return process.env.DEXICON_TOKEN.trim();

  const env = join(REPO, '.env');
  if (!existsSync(env)) {
    throw new Error('No DEXICON_TOKEN set and no .env file. See .env.example.');
  }
  for (const line of readFileSync(env, 'utf8').split('\n')) {
    const trimmed = line.trim();
    if (trimmed.startsWith('DEXICON_BOOTSTRAP_TOKEN=') && !trimmed.startsWith('#')) {
      const value = trimmed.slice('DEXICON_BOOTSTRAP_TOKEN='.length).trim().replace(/^['"]|['"]$/g, '');
      if (value) return value;
    }
  }
  throw new Error(
    'DEXICON_BOOTSTRAP_TOKEN is blank in .env, so the server generated one and logged it once:\n' +
    "    docker compose logs dexicon | grep 'bootstrap token'",
  );
}

/**
 * Resolve playwright wherever it was installed.
 *
 * ESM resolves from the IMPORTING module's directory, not the working directory, so a
 * script in scripts/ does not see clients/web-ui/node_modules. Both are tried, because
 * `--no-save` puts it wherever the install was run.
 */
let chromium;
const candidates = [
  'playwright',
  pathToFileURL(join(REPO, 'clients', 'web-ui', 'node_modules', 'playwright', 'index.mjs')).href,
];

for (const where of candidates) {
  try {
    ({ chromium } = await import(where));
    if (chromium) break;
  } catch { /* try the next one */ }
}

if (!chromium) {
  console.error(
    'playwright is not installed. It is intentionally not a dependency:\n' +
    '    cd clients/web-ui && npm install --no-save playwright && npx playwright install chromium',
  );
  process.exit(1);
}

const bearer = token();

// Fail here rather than screenshotting a sign-in page that looks like a product shot.
const probe = await fetch(`${BASE}/api/corpora`, { headers: { Authorization: `Bearer ${bearer}` } });
if (!probe.ok) {
  console.error(`${BASE} answered ${probe.status} for /api/corpora. Is the stack up, and is .env current?`);
  process.exit(1);
}

mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch();
const context = await browser.newContext({
  viewport: { width: 1440, height: 900 },
  deviceScaleFactor: 2,                    // a 1x screenshot looks soft on any modern display
  colorScheme: 'dark',
});

// The page stores it exactly where the sign-in form would.
await context.addInitScript((value) => {
  try {
    sessionStorage.setItem('dexicon.token', value);
  } catch { /* private mode; the shot will show the sign-in gate and the run will say so */ }
}, bearer);

const page = await context.newPage();

async function shot(name, { height = 900, prepare }) {
  await page.setViewportSize({ width: 1440, height });

  // NOT networkidle: the app holds a server-sent-events connection open for live job
  // progress, so the network is never idle and waiting for it always times out.
  await page.goto(BASE, { waitUntil: 'domcontentloaded' });
  await page.getByRole('button', { name: 'Corpora' }).waitFor({ timeout: 30_000 });

  if (await page.getByRole('button', { name: /^Sign in$/ }).count()) {
    throw new Error('the page is showing the sign-in gate — the token did not take');
  }

  await prepare(page);
  const file = join(OUT, `${name}.png`);
  await page.screenshot({ path: file });
  console.log(`  ${name}.png`);
}

/**
 * A screenshot of a search result REDISTRIBUTES whatever it matched.
 *
 * The first version of this script shot a corpus of published books, and the committed
 * image carried several hundred words of two of them into a repository about to be made
 * public. The retrieval was excellent and the picture was an unlicensed reproduction.
 *
 * So the corpus is named here rather than chosen at the keyboard, and it is this project's
 * own documentation — Apache-2.0, ours to publish. Override it only with content you hold
 * the rights to distribute, which is a narrower set than "content you can legally read".
 */
const CORPUS = process.env.DEXICON_SHOOT_CORPUS ?? 'docs';
const QUESTION = 'how does a corpus get re-indexed when a file changes';

async function search(p) {
  await p.getByPlaceholder(/Ask a question/).fill(QUESTION);
  await p.getByRole('button', { name: /^Search$/ }).last().click();
  await p.getByText(/results ·/).waitFor({ timeout: 120_000 });
}

console.log(`Shooting ${BASE}:`);

await shot('search', {
  prepare: async (p) => {
    // Scoped, never "all visible corpora": an unscoped search can surface anything on
    // the machine, and the screenshot publishes whatever it surfaced.
    await p.getByLabel('Corpus scope').click();
    await p.getByRole('option', { name: CORPUS }).click();

    // Twice. The first query of a cold instance pays for the embedding model waking up —
    // six seconds, printed next to the result count — which is a property of the machine
    // rather than of the product.
    await search(p);
    await p.getByPlaceholder(/Ask a question/).fill('');
    await search(p);
    await p.waitForTimeout(400);
  },
});

await browser.close();
console.log(`\nWritten to ${OUT}`);
