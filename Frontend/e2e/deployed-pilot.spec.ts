import { expect, test } from '@playwright/test';
import { assertReleaseHealth, deployedContract, isAllowedDeployedRequest } from './support/acceptance-boundary.mjs';
import { loginThroughUi } from './support/login';

test('runtime answers for the expected release with genuine scanner readiness', async ({ request }) => {
  const contract = deployedContract();
  const identity = await request.get(`${contract.apiURL}/build-identity`, { maxRedirects: 0 });
  const readiness = await request.get(`${contract.apiURL}/ready`, { maxRedirects: 0 });
  expect(identity.status()).toBe(200);
  expect(readiness.status()).toBe(200);
  assertReleaseHealth(await identity.json(), await readiness.json(), contract.expectedSha);
});

for (const role of ['manager', 'editor', 'denied']) {
  test(`${role}: actual role, tenant and read-only browser boundaries`, async ({ page, context }) => {
    const contract = deployedContract();
    const persona = contract.roles.find((candidate) => candidate.role === role)!;
    const blockedWrites: string[] = [];
    const failures: string[] = [];
    await context.route('**/*', async (route) => {
      const request = route.request();
      if (!isAllowedDeployedRequest(request.url(), request.method(), contract.apiURL)) {
        blockedWrites.push(`${request.method()} ${new URL(request.url()).pathname}`);
        await route.abort('blockedbyclient');
      } else await route.continue();
    });
    page.on('pageerror', () => failures.push('Uncaught page error'));
    page.on('response', (response) => {
      if (response.status() >= 500 && new URL(response.url()).origin === contract.apiURL)
        failures.push(`API ${response.status()} ${new URL(response.url()).pathname}`);
    });
    const permissionsResponse = page.waitForResponse((response) =>
      response.url() === `${contract.apiURL}/api/User/me/permissions` && response.request().method() === 'GET');
    await loginThroughUi(page, persona);
    const response = await permissionsResponse;
    expect(response.status()).toBe(200);
    const permissions = await response.json();
    expect(permissions.businessUnitId).toBe(Number(persona.businessUnitId));
    expect(permissions.roleName).toBe(persona.roleName);
    expect(permissions.isSuperAdmin).toBe(false);
    expect(permissions.hasModuleAuthorityByRank).not.toBe(true);
    expect(permissions.isManager).toBe(persona.role === 'manager');

    // Reuse this test account's own HTTP credentials in memory only, never in artifacts.
    const authorization = (await response.request().allHeaders()).authorization;
    expect(Boolean(authorization)).toBe(true);
    const deniedUsers = await context.request.get(`${contract.apiURL}/api/User?pageNumber=1&pageSize=1`, {
      headers: { Authorization: authorization }, maxRedirects: 0,
    });
    expect(deniedUsers.status()).toBe(403);

    if (persona.role !== 'denied') {
      for (const destination of ['/procurement/leads/all', '/sales/quotes', '/sales/today']) {
        await page.goto(destination);
        await expect(page).toHaveURL(new URL(destination, contract.baseURL).href);
        await expect(page.getByRole('main')).toBeVisible();
        await expect(page.getByRole('main').getByRole('heading').first()).toBeVisible();
        await expect(page.getByRole('heading', { name: /Access Denied|could not check your access/ })).toHaveCount(0);
        await expect(page.getByRole('alert')).toHaveCount(0);
      }
    } else {
      await page.goto('/procurement/leads/all');
      await expect(page.getByRole('heading', { name: 'Access Denied', exact: true })).toBeVisible();
    }
    await page.goto('/sales/team');
    if (persona.role === 'manager') {
      await expect(page.getByRole('main').getByRole('heading').first()).toBeVisible();
      await expect(page.getByRole('heading', { name: 'Manager access required' })).toHaveCount(0);
      await expect(page.getByRole('alert')).toHaveCount(0);
    } else {
      await expect(page.getByRole('heading', { name: 'Manager access required' })).toBeVisible();
    }
    await page.goto('/admin/operations');
    await expect(page.getByRole('heading', { name: 'Access Denied', exact: true })).toBeVisible();
    expect(blockedWrites, 'No business write may be attempted by the deployed read-only lane').toEqual([]);
    expect(failures, 'No browser crash or backend 5xx is acceptable').toEqual([]);
  });
}
