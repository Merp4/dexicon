import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { accepted, advisories, check } from './npm-audit-gate.mjs';

const PICOMATCH = 'GHSA-fc5q-39xc-vjpf';
const SCRIPT_DIR = dirname(fileURLToPath(import.meta.url));

const advisory = (id, name, severity) => ({
  source: 1,
  name,
  dependency: name,
  title: `${name} advisory`,
  url: `https://github.com/advisories/${id}`,
  severity,
  range: '*',
});

/**
 * A version 2 report with one vulnerable package per package name, holding every advisory given
 * for it, and the derived package that depends on the first.
 */
const report = (...found) => {
  const vulnerabilities = {};
  const counts = { info: 0, low: 0, moderate: 0, high: 0, critical: 0 };
  for (const a of found) {
    vulnerabilities[a.name] ??= { name: a.name, severity: a.severity, via: [] };
    vulnerabilities[a.name].via.push(a);
    counts[a.severity] += 1;
  }
  if (found.length > 0) {
    vulnerabilities.vite = { name: 'vite', severity: found[0].severity, via: [found[0].name] };
    counts[found[0].severity] += 1;
  }
  return { auditReportVersion: 2, vulnerabilities, metadata: { vulnerabilities: counts } };
};

test('the accepted advisory alone passes and is reported as accepted', () => {
  const result = check(report(advisory(PICOMATCH, 'picomatch', 'high')));
  assert.deepEqual(result.rejected, []);
  assert.deepEqual(result.tolerated.map((a) => a.id), [PICOMATCH]);
});

test('another high advisory fails even when the accepted one is present', () => {
  const result = check(report(
    advisory(PICOMATCH, 'picomatch', 'high'),
    advisory('GHSA-aaaa-bbbb-cccc', 'left-pad', 'high')));
  assert.deepEqual(result.rejected.map((a) => a.id), ['GHSA-aaaa-bbbb-cccc']);
  assert.deepEqual(result.tolerated.map((a) => a.id), [PICOMATCH]);
});

test('a critical advisory that is not accepted fails', () => {
  const result = check(report(advisory('GHSA-aaaa-bbbb-cccc', 'left-pad', 'critical')));
  assert.equal(result.rejected.length, 1);
});

test('the accepted id under a different package name fails', () => {
  const result = check(report(advisory(PICOMATCH, 'not-picomatch', 'high')));
  assert.equal(result.rejected.length, 1);
});

test('an empty list of accepted advisories fails the picomatch report', () => {
  const result = check(report(advisory(PICOMATCH, 'picomatch', 'high')), new Map());
  assert.deepEqual(result.rejected.map((a) => a.id), [PICOMATCH]);
});

test('advisories below high are ignored, as with --audit-level=high', () => {
  const result = check(report(
    advisory('GHSA-aaaa-bbbb-cccc', 'left-pad', 'moderate'),
    advisory('GHSA-dddd-eeee-ffff', 'right-pad', 'low')));
  assert.deepEqual(result, { rejected: [], tolerated: [] });
});

test('a clean report passes', () => {
  const result = check({ auditReportVersion: 2, vulnerabilities: {}, metadata: { vulnerabilities: { high: 0, critical: 0 } } });
  assert.deepEqual(result, { rejected: [], tolerated: [] });
});

test('an advisory seen under several packages is counted once', () => {
  const a = advisory('GHSA-aaaa-bbbb-cccc', 'left-pad', 'high');
  const r = report(a);
  r.vulnerabilities.other = { name: 'other', severity: 'high', via: [a] };
  assert.equal(advisories(r).length, 1);
});

test('input that is not a version 2 audit report throws', () => {
  assert.throws(() => check(null));
  assert.throws(() => check('text'));
  assert.throws(() => check({}));
  assert.throws(() => check({ auditReportVersion: 1, vulnerabilities: {} }));
  assert.throws(() => check({ error: { code: 'ENOAUDIT', summary: 'audit endpoint returned an error' } }), /ENOAUDIT/);
});

test('a report that counts high findings but names no advisory throws', () => {
  const unreadable = {
    auditReportVersion: 2,
    vulnerabilities: { picomatch: { name: 'picomatch', severity: 'high', via: [] } },
    metadata: { vulnerabilities: { high: 1, critical: 0 } },
  };
  assert.throws(() => check(unreadable), /names no advisory/);
});

test('one advisory reported for the accepted package and for another package fails for the other, in either order', () => {
  for (const names of [['other', 'picomatch'], ['picomatch', 'other']]) {
    const result = check(report(...names.map((name) => advisory(PICOMATCH, name, 'high'))));
    assert.deepEqual(result.rejected.map((a) => a.package), ['other'], names.join());
    assert.deepEqual(result.tolerated.map((a) => a.package), ['picomatch'], names.join());
  }
});

test('a package rated high that leads to no advisory throws, next to the accepted advisory too', () => {
  const r = report(advisory(PICOMATCH, 'picomatch', 'high'));
  r.vulnerabilities.mystery = { name: 'mystery', severity: 'critical', via: [] };
  assert.throws(() => check(r), /mystery is rated critical and names no advisory/);

  r.vulnerabilities.mystery = { name: 'mystery', severity: 'critical', via: ['nowhere'] };
  assert.throws(() => check(r), /mystery is rated critical and names no advisory/);
});

test('another advisory for the accepted package fails', () => {
  const result = check(report(advisory(PICOMATCH, 'picomatch', 'high'), advisory('GHSA-aaaa-bbbb-cccc', 'picomatch', 'high')));
  assert.deepEqual(result.rejected.map((a) => a.id), ['GHSA-aaaa-bbbb-cccc']);
  assert.deepEqual(result.tolerated.map((a) => a.id), [PICOMATCH]);
});

test('the accepted advisory rescored to critical fails', () => {
  const result = check(report(advisory(PICOMATCH, 'picomatch', 'critical')));
  assert.deepEqual(result.rejected.map((a) => a.id), [PICOMATCH]);
  assert.deepEqual(result.tolerated, []);
});

test('a package rated high whose only advisory is moderate throws', () => {
  const r = report(advisory('GHSA-aaaa-bbbb-cccc', 'left-pad', 'moderate'));
  r.vulnerabilities.derived = { name: 'derived', severity: 'high', via: ['left-pad'] };
  assert.throws(() => check(r), /derived is rated high and names no advisory/);
});

test('a package severity that npm does not report throws', () => {
  const r = report(advisory(PICOMATCH, 'picomatch', 'high'));
  for (const severity of ['HIGH', 'severe', undefined]) {
    r.vulnerabilities.odd = { name: 'odd', severity, via: [] };
    assert.throws(() => check(r), /odd has the severity/, String(severity));
  }
});

test('a report that counts high or critical findings but lists no package throws', () => {
  for (const counts of [{ high: 3, critical: 0 }, { high: 0, critical: 2 }]) {
    const empty = { auditReportVersion: 2, vulnerabilities: {}, metadata: { vulnerabilities: counts } };
    assert.throws(() => check(empty), /names no advisory/, JSON.stringify(counts));
  }
});

test('a dependency cycle among packages with no advisory ends and throws', () => {
  const cycle = {
    auditReportVersion: 2,
    vulnerabilities: {
      a: { name: 'a', severity: 'high', via: ['b'] },
      b: { name: 'b', severity: 'high', via: ['a'] },
    },
    metadata: { vulnerabilities: { high: 2, critical: 0 } },
  };
  assert.throws(() => check(cycle), /names no advisory/);
});

test('an advisory severity that npm does not report throws, under a package rated with a valid one', () => {
  for (const severity of ['severe', 'High', 'HIGH', undefined]) {
    const r = report(advisory(PICOMATCH, 'picomatch', 'high'));
    r.vulnerabilities.picomatch.via.push(advisory('GHSA-aaaa-bbbb-cccc', 'picomatch', severity));
    assert.throws(() => check(r), /Advisory .* has the severity/, String(severity));
  }
});

test('every accepted entry names its package, a reason and the condition that removes it', () => {
  for (const [id, entry] of accepted) {
    assert.match(id, /^GHSA-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}$/);
    assert.ok(entry.package);
    assert.ok(['high', 'critical'].includes(entry.severity));
    assert.match(entry.reason, /Remove this entry once/);
  }
});

/** Runs the gate as CI does, on a report file, and returns its exit code and output. */
const runGate = (t, text, script = join(SCRIPT_DIR, 'npm-audit-gate.mjs')) => {
  const dir = mkdtempSync(join(tmpdir(), 'audit-gate-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  const file = join(dir, 'audit.json');
  writeFileSync(file, text);
  const run = spawnSync(process.execPath, [script, file], { encoding: 'utf8' });
  return { code: run.status, out: run.stdout + run.stderr };
};

const jsonOf = (r) => JSON.stringify(r);

test('the command exits 0 for the accepted advisory alone and shows it as a warning', (t) => {
  const run = runGate(t, jsonOf(report(advisory(PICOMATCH, 'picomatch', 'high'))));
  assert.equal(run.code, 0, run.out);
  assert.match(run.out, /::warning::.*GHSA-fc5q-39xc-vjpf/);
});

test('the command exits 0 for a clean report', (t) => {
  const run = runGate(t, jsonOf({ auditReportVersion: 2, vulnerabilities: {}, metadata: { vulnerabilities: {} } }));
  assert.equal(run.code, 0, run.out);
});

test('the command exits 1 and names an advisory that is not accepted', (t) => {
  const run = runGate(t, jsonOf(report(
    advisory(PICOMATCH, 'picomatch', 'high'), advisory('GHSA-aaaa-bbbb-cccc', 'left-pad', 'critical'))));
  assert.equal(run.code, 1, run.out);
  assert.match(run.out, /::error::.*GHSA-aaaa-bbbb-cccc/);
});

test('the command exits 1 for output it cannot read', (t) => {
  for (const text of ['', '{', 'null', '{"error":{"code":"ENOAUDIT"}}', '{"auditReportVersion":1,"vulnerabilities":{}}']) {
    const run = runGate(t, text);
    assert.equal(run.code, 1, `${text}\n${run.out}`);
    assert.match(run.out, /::error::/, text);
  }
});

test('the command exits 2 when no file is named', () => {
  const run = spawnSync(process.execPath, [join(SCRIPT_DIR, 'npm-audit-gate.mjs')], { encoding: 'utf8' });
  assert.equal(run.status, 2);
});

test('the command still runs when it is started through a symbolic link', (t) => {
  const dir = mkdtempSync(join(tmpdir(), 'audit-gate-link-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  const link = join(dir, 'scripts');
  try {
    // The junction type needs no privilege on Windows and is ignored elsewhere.
    symlinkSync(SCRIPT_DIR, link, 'junction');
  } catch (error) {
    t.skip(`cannot create a link here: ${error.code}`);
    return;
  }

  const run = runGate(t, '{"error":{"code":"ENOAUDIT"}}', join(link, 'npm-audit-gate.mjs'));
  assert.equal(run.code, 1, run.out);
  assert.match(run.out, /::error::/);
});
