import fs from 'node:fs/promises';
import path from 'node:path';
import { test as setup } from '@playwright/test';
import { authStatePath, credentialsFor, type RoleName } from './support/environment';
import { loginThroughUi } from './support/login';

const roles: RoleName[] = ['manager', 'editor', 'denied'];

for (const role of roles) {
  setup(`authenticate ${role}`, async ({ page }) => {
    const statePath = authStatePath(role);
    await fs.mkdir(path.dirname(statePath), { recursive: true });
    const credentials = credentialsFor(role);
    if (!credentials) throw new Error(`Missing required ${role} credentials for browser acceptance.`);
    await loginThroughUi(page, credentials);
    await page.context().storageState({ path: statePath });
  });
}
