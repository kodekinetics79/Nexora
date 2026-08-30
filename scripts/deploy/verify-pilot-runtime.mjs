import { mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { assertReleaseHealth, deployedContract } from '../../Frontend/e2e/support/acceptance-boundary.mjs';

const contract = deployedContract(process.env, { withRoles: process.argv.includes('--roles') });
if (process.argv.includes('--preflight')) {
  console.log('Deployed acceptance configuration is complete; no requests were made.');
} else {
  const get = async (url) => {
    const response = await fetch(url, { redirect: 'error', signal: AbortSignal.timeout(20_000) });
    if (response.status !== 200) throw new Error(`Release probe returned HTTP ${response.status}.`);
    return response;
  };
  const getJson = async (url) => {
    const response = await get(url);
    if (!response.headers.get('content-type')?.includes('application/json'))
      throw new Error('Release probe returned a non-JSON response; an SPA fallback is not proof.');
    return response.json();
  };
  const identity = await getJson(`${contract.apiURL}/build-identity`);
  const readiness = await getJson(`${contract.apiURL}/ready`);
  const checks = assertReleaseHealth(identity, readiness, contract.expectedSha);
  const login = await get(`${contract.baseURL}/login`);
  const html = await login.text();
  if (!login.headers.get('content-type')?.includes('text/html') || !html.includes('id="root"'))
    throw new Error('Frontend login did not return the Nexora SPA.');
  const entryPath = html.match(/<script\b[^>]*src="(\/assets\/[^"?#]+\.js)"/i)?.[1];
  if (!entryPath) throw new Error('Frontend module entry is missing.');
  const entry = await get(new URL(entryPath, contract.baseURL));
  if (!/javascript/.test(entry.headers.get('content-type') || ''))
    throw new Error('Frontend module entry returned an invalid content type.');
  const headers = ['content-security-policy', 'x-content-type-options', 'x-frame-options'];
  if (headers.some((name) => !login.headers.get(name))) throw new Error('Frontend security headers are missing.');
  const evidence = {
    checkedAt: new Date().toISOString(),
    expectedBackendSha: contract.expectedSha,
    observedBackendSha: identity.revision,
    frontendOrigin: contract.baseURL,
    frontendEntry: entryPath,
    frontendShaVerified: false,
    note: 'Runtime probe only. Frontend SHA requires Vercel deployment metadata. This is not role or journey certification.',
    checks,
    securityHeadersPresent: headers,
  };
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
  const directory = path.join(root, 'Frontend/test-results/deployed-pilot');
  await mkdir(directory, { recursive: true });
  await writeFile(path.join(directory, 'runtime.json'), `${JSON.stringify(evidence, null, 2)}\n`, { mode: 0o600 });
  console.log(JSON.stringify(evidence, null, 2));
}
