import test from 'node:test';
import assert from 'node:assert/strict';
import console from 'node:console';
import { assertReleaseHealth, deployedContract, isAllowedDeployedRequest, requireDisposableTargets } from '../e2e/support/acceptance-boundary.mjs';
import ZeroSkipsReporter from '../e2e/support/zero-skips-reporter.ts';

const sha = 'a'.repeat(40);
const baseline = () => ({
  PILOT_EXPECTED_SHA: sha,
  PILOT_FRONTEND_URL: 'https://nexora1-ai.vercel.app',
  PILOT_API_URL: 'https://nexora-fyjw.onrender.com',
  ...Object.fromEntries(['MANAGER', 'EDITOR', 'DENIED'].flatMap((role) => [
    [`PILOT_${role}_EMAIL`, `${role.toLowerCase()}@example.com`],
    [`PILOT_${role}_PASSWORD`, 'test-only-not-a-real-password'],
    [`PILOT_${role}_BUSINESS_UNIT_ID`, '99'],
    [`PILOT_${role}_ROLE_NAME`, role],
  ])),
});
const healthy = () => ({ status: 'Healthy', failing: [], checks: [
  'database', 'evidence-storage', 'malware-scanner', 'email-poll-channel', 'extraction-worker',
  'background-workers', 'ocr-engine', 'outbound-email', 'procurement-dispatch-worker',
  'quote-delivery-worker', 'storage-capacity',
].map((name) => ({ name, status: 'Healthy', description: name === 'malware-scanner'
  ? 'ClamAV malware scanner passed clean and detection controls.' : 'healthy' })) });

test('disposable defaults and explicit loopback targets remain usable', () => {
  requireDisposableTargets({});
  requireDisposableTargets({ E2E_BASE_URL: 'http://localhost:5173', E2E_API_URL: 'http://[::1]:5192' });
});
for (const name of ['E2E_BASE_URL', 'E2E_API_URL']) {
  for (const value of ['https://nexora1-ai.vercel.app', 'https://nexora-fyjw.onrender.com',
    'http://localhost.evil.example', 'http://localhost@evil.example', 'file:///tmp/test',
    'http://127.0.0.1:5173/path', 'http://127.0.0.1:5173?url=external']) {
    test(`disposable lane rejects unsafe ${name}: ${value}`, () => {
      assert.throws(() => requireDisposableTargets({ [name]: value }));
    });
  }
}
test('deployed configuration has no fixture credentials or localhost fallback', () => {
  assert.equal(deployedContract(baseline()).roles.length, 3);
  for (const name of Object.keys(baseline())) {
    const env = baseline(); delete env[name];
    assert.throws(() => deployedContract(env), new RegExp(name));
  }
});
test('deployed targets cannot redirect credentials and SHA cannot be abbreviated', () => {
  for (const value of ['https://evil.example', 'https://nexora1-ai.vercel.app@evil.example',
    'https://nexora1-ai.vercel.app?redirect=evil', 'http://nexora1-ai.vercel.app'])
    assert.throws(() => deployedContract({ ...baseline(), PILOT_FRONTEND_URL: value }));
  assert.throws(() => deployedContract({ ...baseline(), PILOT_EXPECTED_SHA: 'abcdef1' }));
});
test('one account cannot masquerade as three roles and tenant identities must agree', () => {
  assert.throws(() => deployedContract({ ...baseline(), PILOT_EDITOR_EMAIL: 'MANAGER@example.com' }));
  assert.throws(() => deployedContract({ ...baseline(), PILOT_EDITOR_BUSINESS_UNIT_ID: '100' }));
  assert.throws(() => deployedContract({ ...baseline(), PILOT_EDITOR_BUSINESS_UNIT_ID: '0' }));
});
test('read-only lane allows login only at the reviewed API and blocks all other writes', () => {
  const api = baseline().PILOT_API_URL;
  assert.equal(isAllowedDeployedRequest(`${api}/api/Auth/Login`, 'POST', api), true);
  assert.equal(isAllowedDeployedRequest(`${api}/api/User/me/permissions`, 'GET', api), true);
  for (const method of ['POST', 'PUT', 'PATCH', 'DELETE'])
    assert.equal(isAllowedDeployedRequest(`${api}/api/orders/1`, method, api), false);
  assert.equal(isAllowedDeployedRequest('https://evil.example/api/Auth/Login', 'POST', api), false);
  assert.equal(isAllowedDeployedRequest(`${api}/api/Auth/Login?next=evil`, 'POST', api), false);
});
test('runtime evidence requires exact SHA, all checks and genuine malware detection', () => {
  const identity = { revision: sha, environment: 'Production' };
  assert.equal(assertReleaseHealth(identity, healthy(), sha).length, 11);
  assert.throws(() => assertReleaseHealth({ ...identity, revision: 'b'.repeat(40) }, healthy(), sha));
  assert.throws(() => assertReleaseHealth({ ...identity, environment: 'Development' }, healthy(), sha));
  for (let i = 0; i < healthy().checks.length; i++) {
    const readiness = healthy(); readiness.checks.splice(i, 1);
    assert.throws(() => assertReleaseHealth(identity, readiness, sha));
  }
  const readiness = healthy(); readiness.checks[2].description = 'BuiltIn structural scan passed.';
  assert.throws(() => assertReleaseHealth(identity, readiness, sha));
  assert.throws(() => assertReleaseHealth(identity, { ...healthy(), failing: ['database'] }, sha));
});

test('deployed reporter accepts exactly four executed tests', () => {
  const reporter = new ZeroSkipsReporter({ expectedTests: 4 });
  reporter.onBegin({}, { allTests: () => Array(4).fill({}) });
  assert.equal(reporter.onEnd({ status: 'passed' }), undefined);
});
test('deployed reporter fails partial discovery, including zero tests', (t) => {
  t.mock.method(console, 'error', () => {});
  for (const count of [0, 1, 3, 5]) {
    const reporter = new ZeroSkipsReporter({ expectedTests: 4 });
    reporter.onBegin({}, { allTests: () => Array(count).fill({}) });
    assert.deepEqual(reporter.onEnd({ status: 'passed' }), { status: 'failed' });
  }
});
test('deployed reporter fails skipped tests even when discovery count matches', (t) => {
  t.mock.method(console, 'error', () => {});
  const reporter = new ZeroSkipsReporter({ expectedTests: 4 });
  reporter.onBegin({}, { allTests: () => Array(4).fill({}) });
  reporter.onTestEnd({ titlePath: () => ['manager'] }, { status: 'skipped' });
  assert.deepEqual(reporter.onEnd({ status: 'passed' }), { status: 'failed' });
});
