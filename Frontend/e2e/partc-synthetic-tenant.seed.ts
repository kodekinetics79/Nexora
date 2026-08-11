/**
 * PART C — SYNTHETIC tenant reproduction.
 *
 * ══════════════════════════════════════════════════════════════════════════════
 *  READ THIS BEFORE QUOTING ANY EVIDENCE THIS FILE PRODUCES
 *
 *  The tenant this creates is a SYNTHETIC REPRODUCTION built for certification.
 *  It is NOT the user's original "Noor and Sons", which is absent from every
 *  reachable database and whose failure was never observed by anyone here.
 *
 *  It carries the same NAME so the journey needs no override. It does not carry
 *  the same history, the same data, or the same cause of failure. Nothing
 *  produced downstream of this file may be described as reproducing what
 *  happened to the real tenant.
 * ══════════════════════════════════════════════════════════════════════════════
 *
 * TEST-ONLY. Run once, before `partc-governed-recovery.spec.ts` steps 9-16.
 *
 * The workspace is created through the REAL four-step wizard in a real browser — not by an
 * INSERT, not by a direct API call — so the execution under examination was produced by the
 * same path an operator uses.
 *
 * ── WHY THE FAILURE IS INJECTED AT THE DATABASE, NOT TYPED INTO THE FORM ──────────────────
 *
 * The obvious candidates cannot produce the state PART C needs, and the code says so:
 *
 *   · a founding-admin address that already exists is REJECTED AT SUBMIT with 409
 *     (ProvisioningRequestValidator) — there is no execution row at all, so nothing to recover;
 *   · a taken or reserved slug is likewise a 400/409 at submit;
 *   · a missing or deactivated plan fails at ordinal 0 (`tenant`) with FailureIsTerminal = true
 *     and ZERO committed steps.
 *
 * PART C needs a failure that is (a) mid-execution, so earlier steps have genuinely committed,
 * and (b) NON-terminal, so the operator can fix the cause and resume the SAME execution.
 * TenantProvisioningRunner.Describe treats only SQLSTATE 23505 / 23503 / 42501 as terminal;
 * everything else is retryable. So the fault injected here is `42P01 undefined_table` on the
 * first relation the baseline seeder reads — the same shape as the 42501 missing-GRANT failure
 * the runner's own comments record as having happened in production.
 *
 * The injection is a reversible RENAME performed by the runbook BEFORE this seed runs, and
 * undone as the repair before the resume. It is fault injection into a disposable local
 * database — it is not a change to product code, and it is not a pretend failure: the runner,
 * the diagnostics projection and the resume path all execute for real.
 */

import { expect, test } from '@playwright/test';
import {
  absoluteApi,
  capture,
  platform,
  record,
  requirePartCEnv,
  signInAsPlatformOwner,
  typeVisibly,
} from './support/partc-control-plane';

const TENANT_NAME = process.env.PARTC_TENANT_NAME?.trim() || 'Noor and Sons';
const SLUG = process.env.PARTC_TENANT_SLUG?.trim() || 'noor-and-sons';

/**
 * A genuinely unused address. Submit validation rejects a duplicate outright, so the failure
 * this seed is after has to come from further in — see the header note.
 */
const ADMIN_EMAIL = process.env.PARTC_ADMIN_EMAIL?.trim() || 'noor.founder@noor-and-sons.example';

/**
 * MUI renders `TextField select` as a popover listbox, not a native `<select>`, so the option is
 * a `role=option` in a portal rather than something `selectOption` can reach. (The MFA policy
 * screen's mode control is the deliberate exception — it opts into `native: true` precisely so it
 * can be driven.)
 */
const chooseOption = async (
  page: import('@playwright/test').Page,
  scope: import('@playwright/test').Locator,
  label: string,
  option: RegExp,
  role: 'combobox' = 'combobox',
): Promise<void> => {
  // By ROLE, not by label: 'Plan' also matches the Billable/Trial radio group's description,
  // so getByLabel('Plan') is ambiguous inside the wizard dialog.
  await scope.getByRole(role, { name: label, exact: true }).click();
  await page.getByRole('option', { name: option }).first().click();
};

/** MUI Autocomplete: type to filter, then take the matching option from the portal listbox. */
const pickFromAutocomplete = async (
  page: import('@playwright/test').Page,
  scope: import('@playwright/test').Locator,
  label: string,
  value: string,
): Promise<void> => {
  const input = scope.getByRole('combobox', { name: label, exact: true });
  await input.click();
  await input.fill(value);
  await page.getByRole('option').filter({ hasText: value }).first().click();
};

const owner = () => {
  const env = requirePartCEnv(
    'PART C synthetic tenant seed',
    'E2E_PLATFORM_ADMIN_EMAIL',
    'E2E_PLATFORM_ADMIN_PASSWORD',
    'E2E_PLATFORM_ADMIN_TOTP_SECRET',
  );
  return {
    email: env.E2E_PLATFORM_ADMIN_EMAIL,
    password: env.E2E_PLATFORM_ADMIN_PASSWORD,
    totpSecret: env.E2E_PLATFORM_ADMIN_TOTP_SECRET,
  };
};

test('creates the SYNTHETIC failing provisioning execution through the real wizard', async ({ page }) => {
  test.setTimeout(300_000);

  expect(process.env.E2E_FIXTURE_MODE, 'The seed must run against the real backend.').toBe('false');
  await signInAsPlatformOwner(page, owner());

  // Refuse to run twice. A second synthetic tenant with the same name would break the very
  // duplicate-count assertions this exists to feed.
  const existing = await page.request.get(absoluteApi('/api/platform/tenants'), {
    headers: { Authorization: `Bearer ${await page.evaluate(() => sessionStorage.getItem('nexora_platform_token'))}` },
  });
  const already = ((await existing.json()) as Array<{ name: string }>)
    .filter((tenant) => tenant.name?.trim() === TENANT_NAME);
  if (already.length > 0) {
    test.skip(true, `A tenant named "${TENANT_NAME}" already exists; the seed will not create a second one.`);
  }

  await platform.nav(page, 'Tenants').click();
  await expect(page.getByRole('heading', { name: 'Tenants', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Create Company' }).click();

  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();

  // ---- Step 1 · Company identity -------------------------------------------------------
  await typeVisibly(dialog.getByLabel('Organization name'), TENANT_NAME);
  await typeVisibly(dialog.getByLabel('Workspace slug'), SLUG);
  await typeVisibly(dialog.getByLabel('Registered legal name'), `${TENANT_NAME} Trading Est.`);
  await typeVisibly(dialog.getByLabel('Company contact email'), `accounts@${SLUG}.example`);
  await typeVisibly(dialog.getByLabel('Address line 1'), '1 Certification Way');
  await typeVisibly(dialog.getByLabel('City'), 'Riyadh');
  await chooseOption(page, dialog, 'Country of registration', /saudi/i, 'combobox');
  await capture(page, test.info(), 0, 'seed-company-identity');
  await dialog.getByRole('button', { name: 'Next' }).click();

  // ---- Step 2 · Commercial terms -------------------------------------------------------
  // A Billable tenant REQUIRES a plan, a named billing contact and an internal account owner.
  // The wizard refuses to advance without them — the guardrail that stops a customer being
  // provisioned silently free and unowned. Filling them in is not ceremony; skipping them is
  // how the form tells you it is doing its job.
  await chooseOption(page, dialog, 'Plan', /starter/i, 'combobox');
  await pickFromAutocomplete(page, dialog, 'Time zone', 'Asia/Riyadh');
  await typeVisibly(dialog.getByRole('textbox', { name: 'Billing contact name', exact: true }), 'Noor Finance');
  await typeVisibly(
    dialog.getByRole('textbox', { name: 'Billing contact email', exact: true }),
    `billing@${SLUG}.example`,
  );
  await typeVisibly(
    dialog.getByRole('textbox', { name: 'Account owner (internal)', exact: true }),
    'owner@nexora.local',
  );
  await capture(page, test.info(), 0, 'seed-commercial-terms');
  await dialog.getByRole('button', { name: 'Next' }).click();

  // ---- Step 3 · Founding administrator -------------------------------------------------
  //
  // A valid, unused address: the failure this seed is after happens at `baseline-seed`, two
  // steps later, and only because the runbook has taken a relation out from under the seeder.
  // Activation stays on the default `invite` path so the `invitation` step is real work rather
  // than a Skipped decision.
  await typeVisibly(dialog.getByLabel('First name'), 'Noor');
  await typeVisibly(dialog.getByLabel('Last name'), 'Administrator');
  await typeVisibly(dialog.getByLabel(/work email/i), ADMIN_EMAIL);
  await capture(page, test.info(), 0, 'seed-founding-administrator');
  await dialog.getByRole('button', { name: 'Next' }).click();

  // ---- Step 4 · Review & provision -----------------------------------------------------
  await capture(page, test.info(), 0, 'seed-review');
  await dialog.getByRole('button', { name: 'Create workspace' }).click();

  // The progress dialog opens on the durable execution. Wait for it to reach a resting state.
  const failed = page.getByText(/stopped at/i);
  const succeeded = page.getByText(/workspace ready/i);
  await expect(failed.or(succeeded).first()).toBeVisible({ timeout: 120_000 });
  await capture(page, test.info(), 0, 'seed-execution-outcome');

  await expect(
    failed,
    'The seed did not produce a FAILED provisioning execution. PART C steps 9-16 examine a failed '
    + 'attempt; a successful one is the wrong subject and must not be silently accepted.',
  ).toBeVisible();

  await record(test.info(), 'synthetic-tenant.json', {
    disclaimer: 'SYNTHETIC reproduction created for certification. NOT the user\'s original tenant.',
    name: TENANT_NAME,
    slug: SLUG,
    adminEmail: ADMIN_EMAIL,
    injectedFault: 'public."QuoteConfiguration" renamed away, producing SQLSTATE 42P01 at the baseline-seed step',
  });
});
