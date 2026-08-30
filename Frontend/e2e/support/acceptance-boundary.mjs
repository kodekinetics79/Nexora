import process from 'node:process';
import { URL } from 'node:url';

const loopbackHosts = new Set(['localhost', '127.0.0.1', '[::1]']);
const liveOrigins = {
  frontend: 'https://nexora1-ai.vercel.app',
  api: 'https://nexora-fyjw.onrender.com',
};

function origin(value, name) {
  let parsed;
  try { parsed = new URL(value); } catch { throw new Error(`${name} must be an explicit HTTP(S) origin.`); }
  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password
      || parsed.pathname !== '/' || parsed.search || parsed.hash)
    throw new Error(`${name} must be an HTTP(S) origin without credentials, path, query, or fragment.`);
  return parsed;
}

// The commercial fixture suite posts quotes, orders, receipts and platform lifecycle changes.
// Never offer an override that could accidentally point this suite at customer data.
export function requireDisposableTargets(env = process.env) {
  for (const [name, fallback] of [
    ['E2E_BASE_URL', 'http://127.0.0.1:5173'],
    ['E2E_API_URL', 'http://127.0.0.1:5192'],
  ]) {
    if (!loopbackHosts.has(origin(env[name] || fallback, name).hostname))
      throw new Error(`${name}: mutation-heavy commercial acceptance is loopback-only. Use the read-only deployed lane for production.`);
  }
}

export function deployedContract(env = process.env, { withRoles = true } = {}) {
  const required = (name) => {
    const value = env[name]?.trim();
    if (!value) throw new Error(`Deployed acceptance requires ${name}; no fixture defaults or skips are allowed.`);
    return value;
  };
  const expectedSha = required('PILOT_EXPECTED_SHA');
  if (!/^[a-f0-9]{40}$/.test(expectedSha)) throw new Error('PILOT_EXPECTED_SHA must be the full lowercase Git commit SHA.');
  const baseURL = origin(required('PILOT_FRONTEND_URL'), 'PILOT_FRONTEND_URL').origin;
  const apiURL = origin(required('PILOT_API_URL'), 'PILOT_API_URL').origin;
  if (baseURL !== liveOrigins.frontend || apiURL !== liveOrigins.api)
    throw new Error('Deployed acceptance is restricted to the reviewed Nexora production origins.');
  const roles = withRoles ? ['manager', 'editor', 'denied'].map((role) => {
    const prefix = `PILOT_${role.toUpperCase()}`;
    const email = required(`${prefix}_EMAIL`);
    const password = required(`${prefix}_PASSWORD`);
    const businessUnitId = required(`${prefix}_BUSINESS_UNIT_ID`);
    const roleName = required(`${prefix}_ROLE_NAME`);
    if (!/^[1-9]\d*$/.test(businessUnitId) || !Number.isSafeInteger(Number(businessUnitId)))
      throw new Error(`${prefix}_BUSINESS_UNIT_ID must be a positive safe integer.`);
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) throw new Error(`${prefix}_EMAIL must be an email address.`);
    return { role, email, password, businessUnitId, roleName };
  }) : [];
  if (new Set(roles.map((role) => role.email.toLowerCase())).size !== roles.length)
    throw new Error('Each deployed test persona needs a distinct account; one administrator cannot certify multiple roles.');
  if (new Set(roles.map((role) => role.businessUnitId)).size > 1)
    throw new Error('The three deployed personas must belong to the same isolated pilot tenant.');
  return { expectedSha, baseURL, apiURL, roles };
}

export function isAllowedDeployedRequest(url, method, apiURL) {
  if (['GET', 'HEAD', 'OPTIONS'].includes(method)) return true;
  const target = new URL(url);
  return method === 'POST' && target.origin === apiURL
    && target.pathname === '/api/Auth/Login' && !target.search;
}

export function assertReleaseHealth(identity, readiness, expectedSha) {
  if (identity?.revision !== expectedSha || identity?.environment !== 'Production')
    throw new Error('Backend identity does not match the expected production release.');
  if (readiness?.status !== 'Healthy' || !Array.isArray(readiness.failing)
      || readiness.failing.length !== 0 || !Array.isArray(readiness.checks))
    throw new Error('Backend readiness is not fully healthy.');
  const checks = new Map(readiness.checks.map((check) => [check.name, check]));
  for (const name of ['database', 'evidence-storage', 'malware-scanner', 'email-poll-channel',
    'extraction-worker', 'background-workers', 'ocr-engine', 'outbound-email',
    'procurement-dispatch-worker', 'quote-delivery-worker', 'storage-capacity']) {
    if (checks.get(name)?.status !== 'Healthy') throw new Error(`Readiness check ${name} is missing or not healthy.`);
  }
  if (!/^ClamAV malware scanner passed clean and detection controls\.$/.test(checks.get('malware-scanner').description || ''))
    throw new Error('Pilot certification requires real ClamAV clean and detection controls, not structural inspection.');
  return [...checks.values()].map(({ name, status }) => ({ name, status }));
}
