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
 * SIGN-IN. The admin password (DEXICON_ADMIN_PASSWORD, from the environment and then
 * .env, as in scripts/dev-token.py) is exchanged for a session at POST /api/session inside
 * this process. Only the session reaches the page, through an init script that stores it
 * in sessionStorage['dexicon.token'], where the sign-in form would. Neither value is
 * printed or written to disk. An API key would not do: a key never carries the `admin`
 * scope (docs/decisions.md D-28), so a browser holding one loads the shell and then gets
 * 403 on the admin screens. The session is checked against /api/tokens, which is
 * admin-only, before the browser starts.
 *
 * DEXICON_BASE selects the instance (default http://127.0.0.1:8477). A wrong password is
 * throttled by the server and can take up to 30 seconds to answer.
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

/** Environment first, then .env — the precedence scripts/dev-token.py uses. */
function adminPassword() {
  if (process.env.DEXICON_ADMIN_PASSWORD) return process.env.DEXICON_ADMIN_PASSWORD.trim();

  const env = join(REPO, '.env');
  if (!existsSync(env)) {
    throw new Error('No DEXICON_ADMIN_PASSWORD set and no .env file. See .env.example.');
  }
  for (const line of readFileSync(env, 'utf8').split(/\r?\n/)) {
    const trimmed = line.trim();
    if (trimmed.startsWith('DEXICON_ADMIN_PASSWORD=') && !trimmed.startsWith('#')) {
      const value = trimmed.slice('DEXICON_ADMIN_PASSWORD='.length).trim().replace(/^['"]|['"]$/g, '');
      if (value) return value;
    }
  }
  throw new Error(
    'DEXICON_ADMIN_PASSWORD is blank in .env, so the server generated one on first run and logged it\n' +
    "once, in quotes on the third line: docker compose logs dexicon | grep -A 2 'admin password'\n" +
    'Set it in the environment or in .env.',
  );
}

/**
 * Exchange the password for a session, and prove the session carries admin.
 *
 * Exits with a message that names the status and the instance, never the password or the
 * session. The check goes to an admin-only endpoint on purpose: one a plain key could also
 * read would pass for a credential that then fails on every screen this script drives.
 */
async function adminSession() {
  const password = adminPassword();

  /**
   * Exit once the body is drained and the connection has had a moment to settle: on
   * Windows, process.exit() with a fetch socket still closing aborts node with a libuv
   * assertion and a -1073740791 exit code instead of 1.
   */
  const fail = async (res, message) => {
    await res?.arrayBuffer().catch(() => {});
    console.error(message);
    await new Promise((done) => setTimeout(done, 250));
    process.exit(1);
  };

  let response;
  try {
    response = await fetch(`${BASE}/api/session`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password }),
      signal: AbortSignal.timeout(60_000),
    });
  } catch (error) {
    await fail(null, `Cannot reach ${BASE}: ${error.cause?.code ?? error.name}. Is the stack up?`);
  }
  if (response.status === 401) {
    await fail(response, `${BASE} did not accept the admin password (HTTP 401). Is DEXICON_ADMIN_PASSWORD current?`);
  }
  if (!response.ok) {
    await fail(response, `${BASE}/api/session answered HTTP ${response.status}. Is this the right instance?`);
  }

  const { token } = await response.json();
  if (!token) await fail(null, `${BASE}/api/session answered without a token.`);

  const admin = await fetch(`${BASE}/api/tokens`, { headers: { Authorization: `Bearer ${token}` } });
  if (!admin.ok) {
    await fail(admin, `The session was issued but ${BASE}/api/tokens answered HTTP ${admin.status}.`);
  }
  await admin.arrayBuffer();
  return token;
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

// Fail here rather than screenshotting a sign-in page that looks like a product shot.
const bearer = await adminSession();

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
    throw new Error('the page is showing the sign-in gate: the session did not take');
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
