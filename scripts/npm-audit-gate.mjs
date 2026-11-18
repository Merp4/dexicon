// Gate for `npm audit --json`: fails on an advisory of high or critical severity unless it is
// listed in `accepted`.
//
//   node scripts/npm-audit-gate.mjs <audit.json>
//
// `npm audit --audit-level=high` cannot name an exception, so one advisory with no patched
// release would fail every pull request. CI audits the production dependencies separately
// with no exceptions (`npm audit --omit=dev`), which keeps an entry below limited to a
// package that ships nothing.
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const failing = new Set(['high', 'critical']);

/**
 * Advisories accepted for now, by GHSA id. An entry names the package, the reason and the
 * condition that removes it. An advisory is accepted only when the package name matches too.
 */
export const accepted = new Map([
  [
    'GHSA-fc5q-39xc-vjpf',
    {
      package: 'picomatch',
      reason:
        'ReDoS in extglob matching (CVE-2026-112366), versions up to 4.0.7, with no patched release ' +
        '(checked 2026-11-18). picomatch is reached only through development dependencies (vite, ' +
        'vitest, tinyglobby), and `npm audit --omit=dev` reports nothing. Remove this entry once a ' +
        'picomatch newer than 4.0.7 is in clients/web-ui/package-lock.json.',
    },
  ],
]);

/** The high and critical advisories in an `npm audit --json` report, one per advisory URL. */
export function advisories(report) {
  if (report === null || typeof report !== 'object' || report.error) {
    const detail = report?.error ? `: ${JSON.stringify(report.error)}` : '';
    throw new Error(`The input is not an npm audit report${detail}`);
  }
  if (report.auditReportVersion !== 2 || typeof report.vulnerabilities !== 'object' || report.vulnerabilities === null) {
    throw new Error('The input is not an npm audit report of version 2');
  }

  const found = new Map();
  for (const node of Object.values(report.vulnerabilities)) {
    for (const via of node.via ?? []) {
      // A string names another vulnerable package; an object is the advisory itself.
      if (typeof via !== 'object' || !failing.has(via.severity)) continue;
      found.set(via.url, {
        id: String(via.url).split('/').pop(),
        package: via.name,
        severity: via.severity,
        title: via.title,
        url: via.url,
      });
    }
  }

  // A report that counts high or critical findings and yields no advisory has changed shape.
  // Passing it would turn an unreadable report into a clean one.
  const counts = report.metadata?.vulnerabilities ?? {};
  const counted = (counts.high ?? 0) + (counts.critical ?? 0);
  if (counted > 0 && found.size === 0) {
    throw new Error(`The report counts ${counted} high or critical findings but names no advisory`);
  }

  return [...found.values()];
}

/** Splits the advisories of a report into those that fail the build and those accepted. */
export function check(report, acceptedAdvisories = accepted) {
  const rejected = [];
  const tolerated = [];
  for (const advisory of advisories(report)) {
    const entry = acceptedAdvisories.get(advisory.id);
    if (entry && entry.package === advisory.package) tolerated.push({ ...advisory, reason: entry.reason });
    else rejected.push(advisory);
  }
  return { rejected, tolerated };
}

function main(path) {
  if (!path) {
    console.error('Usage: node scripts/npm-audit-gate.mjs <audit.json>');
    return 2;
  }

  let result;
  try {
    result = check(JSON.parse(readFileSync(path, 'utf8')));
  } catch (error) {
    console.log(`::error::npm audit gate: ${error.message}`);
    return 1;
  }

  for (const a of result.tolerated)
    console.log(`::warning::npm audit gate: accepted ${a.id} (${a.package}, ${a.severity}). ${a.reason}`);
  for (const a of result.rejected)
    console.log(`::error::npm audit gate: ${a.id} (${a.package}, ${a.severity}): ${a.title} ${a.url}`);

  console.log(
    `npm audit gate: ${result.rejected.length} rejected, ${result.tolerated.length} accepted ` +
      'advisories of high or critical severity.');
  return result.rejected.length === 0 ? 0 : 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = main(process.argv[2]);
}
