import { expect, type Page } from '@playwright/test';
import type { LoginCredentials } from './environment';

export async function loginThroughUi(page: Page, credentials: LoginCredentials): Promise<void> {
  await page.goto('/login');
  await page.evaluate(() => {
    localStorage.removeItem('token');
    localStorage.removeItem('userData');
  });
  await page.getByLabel('Email Address').fill(credentials.email);
  // The password field now sits next to a "Show password" reveal button, so a
  // substring getByLabel('Password') matches both. Match on the computed
  // accessible name + role instead (getByLabel with exact:true would miss the
  // input, whose <label> text carries MUI's required asterisk).
  await page.getByRole('textbox', { name: 'Password', exact: true }).fill(credentials.password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();

  const continueButton = page.getByRole('button', { name: 'Continue', exact: true });
  if (await continueButton.isVisible({ timeout: 2_000 }).catch(() => false)) {
    if (!credentials.businessUnitId) {
      throw new Error('Login requires an organization selection; provide the matching *_BUSINESS_UNIT_ID variable.');
    }
    await page.getByRole('combobox').selectOption(credentials.businessUnitId);
    await continueButton.click();
  }

  // The landing route after login is a product/UX choice — it is /dashboard for some roles and
  // /analytics/deadlines for others, and a role landing somewhere else is not an auth failure.
  // Assert the two things that actually define a successful login: we left /login, and a token
  // was issued. Pinning a specific landing route made this helper fail for a perfectly
  // authenticated user and would break every spec each time the landing page is retuned.
  await expect(page).not.toHaveURL(/\/login(?:\?|$)/, { timeout: 20_000 });
  await expect.poll(() => page.evaluate(() => Boolean(localStorage.getItem('token'))),
    { timeout: 20_000 }).toBe(true);
}
