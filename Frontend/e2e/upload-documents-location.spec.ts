import { expect, test } from '@playwright/test';

test('document upload belongs to Leads and keeps tactile interaction feedback', async ({ page }) => {
  await page.goto('/inbox');
  await expect(page.getByRole('heading', { name: 'Inbox', exact: true })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Upload documents' })).toHaveCount(0);

  await page.goto('/procurement/leads/all');
  const upload = page.getByRole('button', { name: 'Upload documents', exact: true });
  await expect(upload).toBeVisible();

  const restingShadow = await upload.evaluate((element) => getComputedStyle(element).boxShadow);
  expect(restingShadow).not.toBe('none');

  const icon = upload.locator('.MuiButton-startIcon');
  await upload.hover();
  await expect.poll(() => icon.evaluate((element) => getComputedStyle(element).transform))
    .not.toBe('none');

  await upload.click();
  await expect(page).toHaveURL(/\/procurement\/leads\/manual-upload$/);
  await expect(page.getByRole('heading', { name: 'Upload documents', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Inbox', exact: true })).toHaveCount(0);

  await page.getByRole('button', { name: 'Back to Leads' }).click();
  await expect(page).toHaveURL(/\/procurement\/leads\/all$/);
});

test('reduced motion removes spatial route movement', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.goto('/procurement/leads/all');

  const routeSurface = page.locator('.nx-route-enter');
  await expect(routeSurface).toBeVisible();
  await expect.poll(() => routeSurface.evaluate((element) => getComputedStyle(element).animationName))
    .toBe('none');
});
