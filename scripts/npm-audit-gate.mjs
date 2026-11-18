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

const severities = new Set(['info', 'low', 'moderate', 'high', 'critical']);
const failing = new Set(['high', 'critical']);

/**
 * Advisories accepted for now, by GHSA id. An entry names the package, the severity it was
 * accepted at, the reason and the condition that removes it. An advisory is accepted only when
 * the package and the severity match too, so a rescored advisory fails again.
 */
export const accepted = new Map([
  [
    'GHSA-fc5q-39xc-vjpf',
    {
      package: 'picomatch',
      severity: 'high',
      reason:
        'ReDoS in extglob matching (CVE-2026-112366), versions up to 4.0.7, with no patched release ' +
        '(checked 2026-11-18). picomatch is reached only through development dependencies (vite, ' +
        'vitest, tinyglobby), and `npm audit --omit=dev` reports nothing. Remove this entry once a ' +
        'picomatch newer than 4.0.7 is in clients/web-ui/package-lock.json.',
    },
  ],
]);

/**
 * The high and critical advisories in an `npm audit --json` report, one per package and advisory
 * URL. Throws when the report is not one this function can read completely.
 */
export function advisories(report) {
  if (report === null || typeof report !== 'object' || report.error) {
    const detail = report?.error ? `: ${JSON.stringify(report.error)}` : '';
    throw new Error(`The input is not an npm audit report${detail}`);
  }
  const nodes = report.vulnerabilities;
  if (report.auditReportVersion !== 2 || typeof nodes !== 'object' || nodes === null) {
    throw new Error('The input is not an npm audit report of version 2');
  }

  const found = new Map();
  for (const node of Object.values(nodes)) {
    for (const via of node.via ?? []) {
      // A string names another vulnerable package; an object is the advisory itself.
      if (typeof via !== 'object') continue;
      if (!severities.has(via.severity)) {
        throw new Error(`Advisory ${via.url} has the severity ${JSON.stringify(via.severity)}, which is not one npm reports`);
      }
      if (!failing.has(via.severity)) continue;
      // The package is part of the key because one advisory can cover several packages.
      found.set(`${via.name} ${via.url}`, {
        id: String(via.url).split('/').pop(),
        package: via.name,
        severity: via.severity,
        title: via.title,
        url: via.url,
      });
    }
  }

  // A package rated high or critical has to lead to an advisory this function read, directly or
  // through the packages it depends on. One that does not means the report has a shape it did not
  // read, and passing it would turn an unreadable report into a clean one.
  const leadsToAdvisory = (name, seen = new Set()) => {
    if (seen.has(name)) return false;
    seen.add(name);
    return (nodes[name]?.via ?? []).some((via) =>
      typeof via === 'object' ? failing.has(via.severity) : leadsToAdvisory(via, seen));
  };
  for (const [name, node] of Object.entries(nodes)) {
    if (!severities.has(node.severity)) {
      throw new Error(`The package ${name} has the severity ${JSON.stringify(node.severity)}, which is not one npm reports`);
    }
    if (failing.has(node.severity) && !leadsToAdvisory(name)) {
      throw new Error(`The package ${name} is rated ${node.severity} and names no advisory`);
    }
  }

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
    const matches = entry && entry.package === advisory.package && entry.severity === advisory.severity;
    if (matches) tolerated.push({ ...advisory, reason: entry.reason });
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
    `npm audit gate: advisories of high or critical severity: ${result.rejected.length} rejected, ` +
      `${result.tolerated.length} accepted.`);
  return result.rejected.length === 0 ? 0 : 1;
}

// `import.meta.main` is true when this file is the entry point, including when it is started
// through a symbolic link, and false when the tests import it.
if (import.meta.main) {
  process.exitCode = main(process.argv[2]);
}
