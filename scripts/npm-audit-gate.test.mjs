import assert from 'node:assert/strict';
import { test } from 'node:test';
import { accepted, advisories, check } from './npm-audit-gate.mjs';

const PICOMATCH = 'GHSA-fc5q-39xc-vjpf';

const advisory = (id, name, severity) => ({
  source: 1,
  name,
  dependency: name,
  title: `${name} advisory`,
  url: `https://github.com/advisories/${id}`,
  severity,
  range: '*',
});

/** A version 2 report with one vulnerable package per advisory, and the derived package that depends on the first. */
const report = (...found) => {
  const vulnerabilities = {};
  const counts = { info: 0, low: 0, moderate: 0, high: 0, critical: 0 };
  for (const a of found) {
    vulnerabilities[a.name] = { name: a.name, severity: a.severity, via: [a] };
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

test('every accepted entry names its package, a reason and the condition that removes it', () => {
  for (const [id, entry] of accepted) {
    assert.match(id, /^GHSA-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}$/);
    assert.ok(entry.package);
    assert.match(entry.reason, /Remove this entry once/);
  }
});
