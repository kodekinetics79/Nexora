import { expect, type Page } from '@playwright/test';
import type { LoginCredentials } from './environment';

export async function loginThroughUi(page: Page, credentials: LoginCredentials): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Email Address').fill(credentials.email);
  await page.getByLabel('Password').fill(credentials.password);
  await page.getByRole('button', { name: 'LOGIN' }).click();

  const continueButton = page.getByRole('button', { name: 'CONTINUE' });
  if (await continueButton.isVisible({ timeout: 2_000 }).catch(() => false)) {
    if (!credentials.businessUnitId) {
      throw new Error('Login requires an organization selection; provide the matching *_BUSINESS_UNIT_ID variable.');
    }
    await page.getByRole('combobox').selectOption(credentials.businessUnitId);
    await continueButton.click();
  }

  await expect(page).toHaveURL(/\/dashboard(?:\?|$)/, { timeout: 20_000 });
  await expect.poll(() => page.evaluate(() => Boolean(localStorage.getItem('token')))).toBe(true);
}
