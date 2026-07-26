import { expect, type APIResponse, type Page } from '@playwright/test';
import { credentialsFor, type RoleName } from './environment';
import { loginThroughUi } from './login';

export const apiUrl = process.env.E2E_API_URL || 'http://127.0.0.1:5192';

export function required(name: string): string {
  const value = process.env[name]?.trim();
  if (!value) throw new Error(`Core acceptance fixture is missing ${name}.`);
  return value;
}

export function requiredNumber(name: string): number {
  const value = Number(required(name));
  if (!Number.isInteger(value) || value <= 0) throw new Error(`${name} must be a positive integer.`);
  return value;
}

export async function loginAs(page: Page, role: RoleName): Promise<string> {
  const credentials = credentialsFor(role);
  if (!credentials) throw new Error(`Missing required ${role} credentials for core browser acceptance.`);
  await loginThroughUi(page, credentials);
  const token = await page.evaluate(() => localStorage.getItem('token'));
  if (!token) throw new Error(`Authenticated ${role} session did not contain an access token.`);
  return token;
}

export async function loginAsOtherTenant(page: Page): Promise<string> {
  const email = required('E2E_OTHER_EMAIL');
  const password = required('E2E_OTHER_PASSWORD');
  const businessUnitId = required('E2E_OTHER_BUSINESS_UNIT_ID');
  await loginThroughUi(page, { email, password, businessUnitId });
  const token = await page.evaluate(() => localStorage.getItem('token'));
  if (!token) throw new Error('Authenticated other-tenant session did not contain an access token.');
  return token;
}

type Method = 'get' | 'post' | 'put' | 'delete';

export async function api(
  page: Page,
  token: string,
  method: Method,
  route: string,
  data?: unknown,
  headers: Record<string, string> = {},
): Promise<APIResponse> {
  return page.request[method](`${apiUrl}${route}`, {
    data,
    headers: { Authorization: `Bearer ${token}`, ...headers },
  });
}

export async function jsonOk<T>(response: APIResponse): Promise<T> {
  expect(response.ok(), `${response.url()} returned ${response.status()}: ${await response.text()}`).toBeTruthy();
  return response.json() as Promise<T>;
}

export function normalized(value: unknown): string {
  return String(value ?? '').trim().replace(/[\s_-]+/g, '').toLowerCase();
}

export function expectTextValue(actual: unknown, expected: string): void {
  expect(normalized(actual)).toBe(normalized(expected));
}

export async function openLead(page: Page, leadId: number): Promise<void> {
  await page.goto(`/procurement/leads/view/${leadId}`);
  await expect(page.getByText('Lead Details Analysis Engine')).toBeVisible();
}

export async function openLeadConversion(page: Page, leadId: number): Promise<void> {
  await page.goto(`/procurement/leads/${leadId}/convert`);
  await expect(page.getByRole('heading', { name: 'Review inquiry and create RFQ' })).toBeVisible();
}

export async function resolutions(page: Page, token: string, aggregate: 'leads' | 'rfqs' | 'quotes', id: number) {
  return jsonOk<Array<Record<string, unknown>>>(await api(page, token, 'get', `/api/inventory-intelligence/${aggregate}/${id}/resolutions`));
}

export async function resolveLead(page: Page, token: string, leadId: number, limit: 10 | 20 | 50 = 10) {
  return jsonOk<Array<Record<string, unknown>>>(
    await api(page, token, 'post', `/api/inventory-intelligence/leads/${leadId}/resolve?limit=${limit}`),
  );
}

export function resolutionFor(rows: Array<Record<string, unknown>>, part: string): Record<string, unknown> {
  const row = rows.find((candidate) => normalized(candidate.requestedPartNumber) === normalized(part));
  expect(row, `No persisted commercial resolution exists for ${part}.`).toBeTruthy();
  return row!;
}
