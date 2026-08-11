/**
 * PART C — governed stale-lease recovery under a relaxed platform MFA policy.
 *
 * TEST-ONLY. Independent SDET harness. This file certifies or rejects; it never repairs.
 *
 * ─────────────────────────────────────────────────────────────────────────────
 * WHAT THIS IS
 *
 * Eighteen numbered steps, each its own `test()` so each one reports its own verdict, all
 * driving ONE real Google Chrome against ONE real backend with real workers. No request
 * interception, no route mocking, no fixture API. What the screen shows is what the server said.
 *
 * Three of the features this journey exercises are being written concurrently by other agents
 * and do not exist on this branch:
 *
 *   A. a platform MFA policy with REQUIRED / OPTIONAL / DISABLED_TEST_ONLY
 *   B. a tenant deployment profile + provisioning failure projection UI
 *   C. governed stale-lease recovery and provisioning resume
 *
 * Every step that depends on one of them PROBES for it and, finding nothing, SKIPS with a
 * message naming every candidate route it tried and the status each returned. A blocked step
 * is never a green step. `partc-step-ledger.ts` prints the executed/blocked matrix at the end.
 *
 * ─────────────────────────────────────────────────────────────────────────────
 * THE TENANT IS A PARAMETER, NOT AN ASSUMPTION
 *
 * PARTC_TENANT_NAME (default "Noor and Sons") names the tenant whose failed provisioning is
 * under examination. If that tenant is not in the database this suite FAILS LOUDLY at step 10
 * and stops. It will not create it, seed it, approximate it, or fall back to another tenant:
 * a recovery journey run against a tenant the harness invented certifies nothing.
 *
 * ─────────────────────────────────────────────────────────────────────────────
 * SAFETY
 *
 * Step 5 relaxes a platform-wide authentication control. `afterAll` restores REQUIRED through
 * the API whatever happened in between, and shouts if it could not — a certification run must
 * not be able to leave the control plane weaker than it found it.
 */

import { expect, test, type Page, type BrowserContext } from '@playwright/test';
import {
  absoluteApi,
  apiUrl,
  blockedByMissingApi,
  blockedByMissingUi,
  candidates,
  capture,
  newJourneyState,
  platform,
  probeApi,
  probeConsoleRoute,
  platformBearer,
  record,
  requirePartCEnv,
  openViaSidebar,
  settled,
  signInAsPlatformOwner,
  signOutOfPlatformConsole,
  typeVisibly,
  type ApiProbe,
  type PlatformOwnerCredentials,
} from './support/partc-control-plane';

// ───────────────────────────────────────────────────────────────────────────
// The capability contract.
//
// Each entry is an ORDERED candidate list plus the environment variable that overrides it.
// When feature A/B/C lands on a different route, the runner sets one variable — nobody edits
// this spec to make a certification pass, which is the point.
// ───────────────────────────────────────────────────────────────────────────

const CONTRACT = {
  /** A. Platform MFA policy — Owner read + write. Confirmed: PlatformMfaPolicyController. */
  mfaPolicyApi: () =>
    candidates('PARTC_MFA_POLICY_API', [
      '/api/platform/auth/policy',
      '/api/platform/auth/mfa/policy',
      '/api/platform/security/mfa-policy',
    ]),
  /**
   * A. The effective policy, deliberately reachable from a password-only session so the banner
   * can render before the second factor. Read separately from the Owner endpoint precisely
   * because the two have different reachability — asserting on one says nothing about the other.
   */
  mfaEffectiveApi: () =>
    candidates('PARTC_MFA_EFFECTIVE_API', [
      '/api/platform/auth/policy/effective',
      '/api/platform/auth/mfa/policy/effective',
    ]),
  /** A. The console surface. Confirmed: PlatformRoutes `security/authentication`. */
  mfaPolicyRoutes: () =>
    candidates('PARTC_MFA_POLICY_ROUTE', [
      '/platform/security/authentication',
      '/platform/security/platform-authentication',
      '/platform/authentication',
    ]),
  /**
   * B. Tenant deployment profile / production-only dependency projection.
   *
   * The tenant-scoped diagnostics route landed first and is checked first: it returns
   * `TenantProvisioningDiagnostics`, which carries `DeploymentProfile`, `ProductionBlockers`
   * and `LocalTestBlockers` — exactly the projection step 16 needs.
   */
  deploymentProfileApi: (tenantId: string) =>
    candidates('PARTC_DEPLOYMENT_PROFILE_API', [
      `/api/platform/provisioning/tenants/${tenantId}/diagnostics`,
      `/api/platform/tenants/${tenantId}/activation/decision`,
      `/api/platform/tenants/${tenantId}/deployment-profile`,
      `/api/platform/tenants/${tenantId}/deployment`,
      `/api/platform/tenants/${tenantId}/dependencies`,
    ]),
  /** Confirmed present: TenantProvisioningController [HttpGet("tenants/{tenantId:long}/diagnostics")]. */
  tenantDiagnostics: (tenantId: string) => `/api/platform/provisioning/tenants/${tenantId}/diagnostics`,
  /** Confirmed present: TenantProvisioningController [HttpGet("executions/{id:long}/diagnostics")]. */
  executionDiagnostics: (id: string) => `/api/platform/provisioning/executions/${id}/diagnostics`,
  /** C. Governed lease recovery. Confirmed: ProvisioningLeaseRecoveryController. */
  leaseRecoveryApi: (executionId: string) =>
    candidates('PARTC_LEASE_RECOVERY_API', [
      `/api/platform/provisioning/executions/${executionId}/lease`,
      `/api/platform/provisioning/executions/${executionId}/recover`,
      `/api/platform/provisioning/executions/${executionId}/takeover`,
    ]),
  /** C. The write. Confirmed: [HttpPost("executions/{id:long}/lease/recover")]. */
  leaseRecover: (executionId: string) =>
    `/api/platform/provisioning/executions/${executionId}/lease/recover`,
  /** Confirmed present on this branch — asserted, not probed, and a 404 here is a real defect. */
  executionsList: '/api/platform/provisioning/executions?take=200',
  execution: (id: string) => `/api/platform/provisioning/executions/${id}`,
  retry: (id: string) => `/api/platform/provisioning/executions/${id}/retry`,
  tenants: '/api/platform/tenants',
  activationDecision: (tenantId: string) => `/api/platform/tenants/${tenantId}/activation/decision`,
  invitations: (tenantId: string) => `/api/platform/tenants/${tenantId}/admin-invitations`,
  tenantBilling: (tenantId: string) => `/api/platform/billing/tenants/${tenantId}`,
  audit: '/api/platform/audit?take=300',
  /** Entitlement snapshot has no platform read surface on this branch; probed, never assumed. */
  entitlementSnapshotApi: (tenantId: string) =>
    candidates('PARTC_ENTITLEMENT_SNAPSHOT_API', [
      `/api/platform/tenants/${tenantId}/entitlements`,
      `/api/platform/tenants/${tenantId}/entitlement-snapshot`,
      `/api/platform/entitlements/tenants/${tenantId}`,
    ]),
};

/**
 * The real control contract, read off `PlatformAuthenticationPage.tsx`.
 *
 * The mode control is a NATIVE `<select>` (`slotProps={{ select: { native: true } }}`), which is
 * why this drives it with `selectOption` on option VALUES rather than clicking text: a native
 * select has no clickable option in a rendered page, and a harness that "clicked" one would be
 * asserting against something the operator never sees.
 *
 * Confirmation phrases are NOT hard-coded here. The server serves them in `confirmationPhrases`
 * and the harness reads them from there, so a phrase change cannot leave this suite green against
 * a control it is no longer typing correctly.
 */
const POLICY_CONTROLS = {
  panel: /platform authentication/i,
  modeSelect: 'MFA enforcement mode',
  // Driven via getByRole, not getByLabel: MUI's multiline TextField renders a second,
  // aria-hidden textarea to measure autosize height, so the label matches two nodes.
  reasonField: 'Reason',
  expiryField: 'Expires at',
  confirmationField: 'Confirmation phrase',
  currentPasswordField: 'Current password',
  applyButton: 'Apply MFA policy',
  statusTestId: 'platform-mfa-policy-status',
  notRelaxableTestId: 'platform-mfa-not-relaxable',
  applyBlockedTestId: 'platform-mfa-apply-blocked',
  bannerTestId: 'platform-mfa-disabled-banner',
  bannerText: 'MFA enforcement is disabled in this test environment.',
};

/** `datetime-local` is WALL CLOCK, not UTC. Feeding it an ISO/UTC string sets the wrong hour. */
const localDateTimeValue = (at: Date): string => {
  const pad = (value: number) => String(value).padStart(2, '0');
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`
    + `T${pad(at.getHours())}:${pad(at.getMinutes())}`;
};

const state = newJourneyState();
let context: BrowserContext;
let page: Page;
/**
 * Resolved on demand, never cached in a module variable that a test assigns.
 *
 * Playwright starts a FRESH WORKER after a failing test in a serial group, which resets module
 * scope. A `credentials` captured in step 1 is therefore `undefined` in Section 3, and the run
 * reports "Cannot read properties of undefined" instead of the audit finding it was asked for.
 */
const resolveCredentials = (): PlatformOwnerCredentials => {
  const env = requirePartCEnv(
    'PART C certification',
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

const step = (ordinal: number, title: string) => `${String(ordinal).padStart(2, '0')} · ${title}`;

/** Records a blocked step in a way the ledger reporter and a human both read. */
const blocked = (reason: string): never => {
  test.info().annotations.push({ type: 'partc-blocked', description: reason });
  test.skip(true, reason);
  throw new Error('unreachable');
};

const partial = (missing: string): void => {
  test.info().annotations.push({ type: 'partc-partial', description: missing });
};

const bearer = async (): Promise<string> => {
  state.bearer = await platformBearer(page);
  return state.bearer;
};

const json = async (path: string): Promise<{ status: number; body: any }> => {
  const response = await page.request.get(absoluteApi(path), {
    headers: { Authorization: `Bearer ${state.bearer ?? (await bearer())}` },
  });
  const body = await response.json().catch(() => null);
  return { status: response.status(), body };
};

test.beforeAll(async ({ browser }) => {
  context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  page = await context.newPage();
});

test.afterAll(async () => {
  // Restoration is unconditional. If step 5 relaxed the policy and step 17 did not run —
  // because a middle step failed, because the operator interrupted the run — the control
  // plane must not be left in DISABLED_TEST_ONLY by a test.
  if (state.policyRelaxed && state.policyApiRoute && state.bearer) {
    // The same contract the screen obeys — mode, reason, the SERVED confirmation phrase and a
    // password re-authentication. A teardown that could skip those would be a back door.
    const response = await page.request
      .fetch(absoluteApi(state.policyApiRoute), {
        method: 'PUT',
        headers: { Authorization: `Bearer ${state.bearer}`, 'Content-Type': 'application/json' },
        data: {
          mode: 'REQUIRED',
          reason: 'PART C harness teardown: restoring enforced multi-factor authentication.',
          confirmation: state.policyPhrases?.REQUIRED ?? 'RESTORE PLATFORM MFA',
          currentPassword: resolveCredentials().password,
        },
      })
      .catch(() => null);
    if (!response?.ok()) {
      console.error(
        '\n*** PART C HARNESS ALARM *** The platform MFA policy was relaxed by this run and could '
        + `NOT be restored to REQUIRED via ${state.policyApiRoute} (status ${response?.status() ?? 'network error'}). `
        + 'Restore it by hand before anyone else uses this environment.\n',
      );
    }
  }
  await context?.close();
});

// ═══════════════════════════════════════════════════════════════════════════
// SECTION 1 — relaxing the platform authentication policy (steps 1-8)
// ═══════════════════════════════════════════════════════════════════════════

test.describe.serial('PART C · Section 1 — platform authentication policy', () => {
  test(step(1, 'the real control plane is up, and this run is not using fixtures'), async () => {
    expect(
      process.env.E2E_FIXTURE_MODE,
      'PART C must run against the real backend. Set E2E_FIXTURE_MODE=false; the fixture API is forbidden in this lane.',
    ).toBe('false');

    const env = requirePartCEnv(
      'PART C certification',
      'E2E_BASE_URL',
      'E2E_API_URL',
      'E2E_PLATFORM_ADMIN_EMAIL',
      'E2E_PLATFORM_ADMIN_PASSWORD',
      'E2E_PLATFORM_ADMIN_TOTP_SECRET',
    );

    const health = await page.request.get(absoluteApi('/health'));
    expect(health.ok(), `The real backend at ${apiUrl()} is not healthy (HTTP ${health.status()}).`).toBeTruthy();

    await record(test.info(), 'partc-parameters.json', {
      baseUrl: env.E2E_BASE_URL,
      apiUrl: apiUrl(),
      tenantUnderExamination: state.tenantName,
      tenantNameOverride: 'PARTC_TENANT_NAME',
    });

    await page.goto('/platform/overview');
    await expect(platform.loginHeading(page)).toBeVisible({ timeout: 30_000 });
    await capture(page, test.info(), 1, 'control-plane-reachable');
  });

  test(step(2, 'Platform Owner signs in, and the server demands the authenticator'), async () => {
    const outcome = await signInAsPlatformOwner(page, resolveCredentials());
    state.baselineChallenged = outcome.authenticatorChallenged;

    await expect(platform.overviewHeading(page)).toBeVisible();
    await bearer();
    await capture(page, test.info(), 2, 'signed-in-as-platform-owner');

    // The baseline for steps 7 and 17. If the platform does NOT challenge here, either the
    // policy is already relaxed or enforcement is broken — both make the rest meaningless.
    expect(
      outcome.authenticatorChallenged,
      'Baseline enforcement is wrong: the Platform Owner signed in without an authenticator challenge '
      + 'BEFORE any policy change. Steps 7 and 17 cannot distinguish a working DISABLED_TEST_ONLY '
      + 'from a broken REQUIRED. Fix enforcement, or re-run against an environment whose policy is REQUIRED.',
    ).toBe(true);
    expect(outcome.enrollmentDemanded, 'The Owner was pushed into MFA enrollment rather than verification.').toBe(false);
  });

  test(step(3, 'Security → Platform Authentication opens'), async () => {
    // Walked the way an operator walks it — Security first, then the link out of it. A page that
    // is only reachable by typing its URL is a page that has not shipped.
    await platform.nav(page, 'Security').click();
    await expect(page).toHaveURL(/\/platform\/security$/);
    await expect(page.getByRole('heading', { name: 'Security', exact: true })).toBeVisible();
    await capture(page, test.info(), 3, 'security-page');

    const link = page.getByRole('link', { name: /Open Platform Authentication/i }).first();
    if (await link.isVisible({ timeout: 5_000 }).catch(() => false)) {
      await link.click();
    } else {
      partial('Security does not link to Platform Authentication; fell back to the sidebar entry.');
      await platform.nav(page, 'Platform Authentication').click();
    }

    const probe = await probeConsoleRoute(page, [page.url(), ...CONTRACT.mfaPolicyRoutes()], (target) =>
      target.getByRole('heading', { name: 'Platform Authentication', exact: true }));
    state.policyRoute = probe.route;
    await record(test.info(), 'platform-authentication-ui-probe.json', probe);

    if (!probe.route) {
      await capture(page, test.info(), 3, 'security-page-without-platform-authentication');
      blocked(
        blockedByMissingUi(
          'Security → Platform Authentication (feature A)',
          probe.tried,
          'PARTC_MFA_POLICY_ROUTE',
        ),
      );
    }

    // The per-operator enrollment panel and the plane-wide policy both say "MFA". Confusing them
    // is how an operator believes they have disabled enforcement when they have unenrolled themselves.
    await expect(page.getByTestId(POLICY_CONTROLS.statusTestId)).toBeVisible();
    await capture(page, test.info(), 3, 'platform-authentication-panel');
  });

  test(step(4, 'the current policy reads REQUIRED'), async () => {
    const probe = await probeApi(page.request, await bearer(), 'PARTC_MFA_POLICY_API', CONTRACT.mfaPolicyApi());
    state.policyApiRoute = probe.route;
    await record(test.info(), 'mfa-policy-api-probe.json', probe);
    await capture(page, test.info(), 4, 'policy-read');

    if (!probe.found) blocked(blockedByMissingApi('the platform MFA policy read (feature A)', probe));
    expect(
      probe.authorized,
      `${probe.route} exists but answered HTTP ${probe.status} to the Platform Owner. A policy the Owner `
      + 'cannot read is a defect, not a missing feature.',
    ).toBe(true);

    const mode = JSON.stringify(probe.body);
    expect(mode, `The policy at ${probe.route} does not report REQUIRED: ${mode}`).toMatch(/REQUIRED/i);
  });

  test(step(5, 'DISABLED_TEST_ONLY is selected with a reason and an expiry'), async () => {
    if (!state.policyRoute) blocked('Step 3 found no Platform Authentication surface, so there is nothing to change.');

    await openViaSidebar(page, 'Platform Authentication', state.policyRoute!);
    await expect(page.getByTestId(POLICY_CONTROLS.statusTestId)).toBeVisible();

    // A production-class deployment renders no disable option at all, by design. That is a
    // correct refusal, not a harness failure — and it must not read as one.
    if (await page.getByTestId(POLICY_CONTROLS.notRelaxableTestId).isVisible().catch(() => false)) {
      await capture(page, test.info(), 5, 'policy-not-relaxable-here');
      blocked(
        'BLOCKED — this deployment is production-class, so DISABLED_TEST_ONLY is unreachable by any route '
        + '(platform-mfa-not-relaxable is rendered). That is the control working. PART C needs a deployment '
        + 'classified LocalOrTest: run against ASPNETCORE_ENVIRONMENT=Development, which is what '
        + 'scripts/local/run-platform-console.sh sets.',
      );
    }

    // The phrases come from the server, never from this file.
    const policy = await probeApi(page.request, await bearer(), 'PARTC_MFA_POLICY_API', CONTRACT.mfaPolicyApi());
    const phrases: Record<string, string> = (policy.body as any)?.confirmationPhrases ?? {};
    const phrase = phrases.DISABLED_TEST_ONLY ?? phrases['DISABLED_TEST_ONLY'];
    expect(
      phrase,
      `The policy read served no confirmation phrase for DISABLED_TEST_ONLY (got ${JSON.stringify(phrases)}). `
      + 'The harness will not guess it: a typed confirmation the harness invents is not a confirmation.',
    ).toBeTruthy();

    const modes: string[] = (policy.body as any)?.availableModes ?? [];
    expect(
      modes,
      `DISABLED_TEST_ONLY is not among the modes this deployment offers (${modes.join(', ')}).`,
    ).toContain('DISABLED_TEST_ONLY');

    const reasonText = 'PART C certification: proving governed provisioning recovery without a second factor.';
    await page.getByLabel(POLICY_CONTROLS.modeSelect).selectOption('DISABLED_TEST_ONLY');
    await page.getByLabel(POLICY_CONTROLS.expiryField)
      .fill(localDateTimeValue(new Date(Date.now() + 60 * 60 * 1000)));
    await typeVisibly(page.getByRole('textbox', { name: POLICY_CONTROLS.reasonField, exact: true }), reasonText);
    await typeVisibly(page.getByLabel(POLICY_CONTROLS.confirmationField), phrase);
    await typeVisibly(page.getByLabel(POLICY_CONTROLS.currentPasswordField), resolveCredentials().password);
    await capture(page, test.info(), 5, 'policy-change-form');

    const apply = page.getByRole('button', { name: POLICY_CONTROLS.applyButton });
    const blockedNotice = page.getByTestId(POLICY_CONTROLS.applyBlockedTestId);
    if (await apply.isDisabled()) {
      const why = (await blockedNotice.textContent().catch(() => null)) ?? '(no reason rendered)';
      await capture(page, test.info(), 5, 'policy-apply-disabled');
      expect(apply, `Apply is disabled with every required field filled in. The console says: ${why}`).toBeEnabled();
    }
    await apply.click();

    // The screen is not the authority. The server is.
    //
    // Asserted on the FIELD, never on `JSON.stringify(body)`: the payload also carries
    // `availableModes` and `confirmationPhrases`, both of which contain the literal string
    // "DISABLED_TEST_ONLY" whatever the mode actually is. A regex over the whole document passes
    // against a policy still set to REQUIRED — a green step proving nothing.
    //
    // Polled, because the click returns before the mutation resolves; reading once races the write.
    await expect
      .poll(
        async () => (await json(CONTRACT.mfaPolicyApi()[0])).body?.mode,
        { message: 'The server did not record DISABLED_TEST_ONLY after the save.', timeout: 20_000 },
      )
      .toBe('DISABLED_TEST_ONLY');

    const after = await probeApi(page.request, await bearer(), 'PARTC_MFA_POLICY_API', CONTRACT.mfaPolicyApi());
    await record(test.info(), 'mfa-policy-after-change.json', after);
    const recorded = after.body as any;
    expect(recorded?.changeReason, 'The server recorded the relaxation without the operator reason.')
      .toContain('PART C certification');
    expect(
      recorded?.expiresAtUtc,
      'The relaxation was recorded with no expiry. A bypass that does not lapse on its own is a permanent one.',
    ).toBeTruthy();
    expect(recorded?.enforcementDisabled, 'The server does not consider enforcement disabled.').toBe(true);

    state.policyRelaxed = true;
    state.policyEverRelaxed = true;
    state.policyApiRoute = after.route;
    state.policyPhrases = phrases;
    await capture(page, test.info(), 5, 'policy-disabled-test-only');
  });

  test(step(6, 'the operator signs out'), async () => {
    if (!state.policyRelaxed) blocked('The policy was never relaxed, so signing out proves nothing about it.');
    await signOutOfPlatformConsole(page);
    const token = await page.evaluate(() => sessionStorage.getItem('nexora_platform_token'));
    expect(token, 'Signing out left the platform bearer in sessionStorage.').toBeNull();
    await capture(page, test.info(), 6, 'signed-out');
  });

  test(step(7, 'signing back in raises NO authenticator challenge'), async () => {
    if (!state.policyRelaxed) blocked('The policy was never relaxed, so the absence of a challenge would prove nothing.');

    const owner = resolveCredentials();
    await page.goto('/platform/overview');
    await expect(platform.loginHeading(page)).toBeVisible({ timeout: 30_000 });
    await typeVisibly(platform.emailField(page), owner.email);
    await typeVisibly(platform.passwordField(page), owner.password);
    await platform.submitCredentials(page).click();

    // Assert the positive outcome first, then that the challenge never existed — checking
    // only for absence would pass against a login that simply had not rendered yet.
    await expect(platform.overviewHeading(page)).toBeVisible({ timeout: 30_000 });
    await expect(
      platform.authenticatorField(page),
      'DISABLED_TEST_ONLY is in force but the console still demanded an authenticator code.',
    ).toHaveCount(0);
    await bearer();
    await capture(page, test.info(), 7, 'signed-in-without-authenticator');
  });

  test(step(8, 'a test-mode warning banner is on screen and says why and until when'), async () => {
    if (!state.policyRelaxed) blocked('The policy was never relaxed, so no test-mode banner is expected.');

    const banner = page.getByTestId(POLICY_CONTROLS.bannerTestId);
    await capture(page, test.info(), 8, 'test-mode-banner');
    await expect(
      banner,
      'The console is running with platform MFA disabled and says nothing about it. An operator cannot '
      + 'tell this environment from a hardened one.',
    ).toBeVisible();

    // role="alert" is the difference between a coloured box and something a screen reader announces.
    await expect(banner, 'The disabled-MFA banner is not announced as an alert.').toHaveAttribute('role', 'alert');
    await expect(banner).toContainText(POLICY_CONTROLS.bannerText);

    const text = (await banner.textContent()) ?? '';
    await record(test.info(), 'test-mode-banner.json', { text });
    expect(text, 'The banner does not say who relaxed enforcement.').toMatch(/set by/i);
    expect(text, 'The banner does not say when enforcement returns.').toMatch(/returns to REQUIRED/i);

    // The banner is rendered by PlatformLayout above <Outlet />, so it owes its warning on every
    // screen — not only the one the operator happened to relax it from.
    await platform.nav(page, 'Tenants').click();
    await expect(
      page.getByTestId(POLICY_CONTROLS.bannerTestId),
      'The disabled-MFA banner vanishes when the operator navigates. A warning that is only on the '
      + 'settings screen is a warning nobody sees while doing the work.',
    ).toBeVisible();
    await capture(page, test.info(), 8, 'test-mode-banner-persists');
  });
});

// ═══════════════════════════════════════════════════════════════════════════
// SECTION 2 — governed recovery of the named tenant (steps 9-16)
//
// A separate serial block on purpose: a missing tenant must not silently swallow the
// enforcement-restoration steps in Section 3, which are the ones that leave the environment safe.
// ═══════════════════════════════════════════════════════════════════════════

test.describe.serial('PART C · Section 2 — provisioning failure and governed recovery', () => {
  test(step(9, 'the Tenants screen opens'), async () => {
    // Section 2 is a separate serial block so a missing tenant cannot swallow Section 3's
    // enforcement restoration. That independence means it must also cope with Section 1
    // having failed at step 1, before credentials were ever resolved.
    if (!state.bearer) {
      await signInAsPlatformOwner(page, resolveCredentials());
      await bearer();
    }
    await platform.nav(page, 'Tenants').click();
    await expect(page).toHaveURL(/\/platform\/tenants$/);
    await expect(page.getByRole('heading', { name: 'Tenants', exact: true })).toBeVisible();
    await capture(page, test.info(), 9, 'tenants-screen');
  });

  test(step(10, `the tenant under examination is found by name`), async () => {
    const { status, body } = await json(CONTRACT.tenants);
    expect(status, `GET ${CONTRACT.tenants} answered HTTP ${status}.`).toBe(200);

    const all: Array<{ id: string | number; name: string }> = Array.isArray(body) ? body : [];
    const matches = all.filter((tenant) => tenant.name?.trim() === state.tenantName);
    await record(test.info(), 'tenant-lookup.json', {
      wanted: state.tenantName,
      matched: matches.length,
      availableNames: all.map((tenant) => tenant.name).sort(),
    });

    await typeVisibly(platform.tenantSearch(page), state.tenantName);
    await capture(page, test.info(), 10, 'tenant-search');

    // Deliberately a hard failure, never a skip and never a substitution.
    expect(
      matches.length,
      `PART C is parameterised on the tenant "${state.tenantName}" and this database does not contain it `
      + `(${all.length} tenants present). The harness will not create it, seed it, or fall back to another `
      + 'tenant — a recovery journey run against an invented tenant certifies nothing. Point the run at a '
      + 'database that holds it, or set PARTC_TENANT_NAME to a tenant that genuinely has a failed '
      + 'provisioning execution.',
    ).toBe(1);

    state.tenantId = String(matches[0].id);
    await expect(platform.tenantRow(page, state.tenantName)).toBeVisible();
    await capture(page, test.info(), 10, 'tenant-found');
  });

  test(step(11, 'the provisioning details for that tenant open'), async () => {
    const { status, body } = await json(CONTRACT.executionsList);
    expect(status, `GET ${CONTRACT.executionsList} answered HTTP ${status}.`).toBe(200);

    const executions: any[] = Array.isArray(body) ? body : [];
    const mine = executions.filter(
      (execution) => String(execution.tenantId ?? '') === state.tenantId || execution.name === state.tenantName,
    );
    await record(test.info(), 'provisioning-executions.json', {
      tenantId: state.tenantId,
      matched: mine.map((e) => ({ id: e.id, state: e.state, failedStep: e.failedStep, name: e.name })),
    });

    expect(
      mine.length,
      `No provisioning execution references tenant ${state.tenantId} ("${state.tenantName}"). PART C examines a `
      + 'FAILED provisioning attempt; without one there is nothing to recover and nothing to certify.',
    ).toBeGreaterThan(0);
    state.executionId = String(mine[0].id);

    // Reached the way an operator reaches it: from the Tenants grid, by clicking the row and then
    // the tab. The defect this step guards against is a failed attempt that is reachable by API
    // and invisible in the console, so navigating by URL would be testing the wrong thing — and a
    // hard `goto` also races the route's React.lazy chunk.
    await platform.nav(page, 'Tenants').click();
    await typeVisibly(platform.tenantSearch(page), state.tenantName);
    await platform.tenantRow(page, state.tenantName)
      .getByText(state.tenantName, { exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/platform/tenants/${state.tenantId}`));
    const openerName = new RegExp(
      process.env.PARTC_PROVISIONING_OPENER?.trim() || '^Provisioning$',
      'i',
    );
    const opener = page
      .getByRole('tab', { name: openerName })
      .or(page.getByRole('button', { name: openerName }))
      .or(page.getByRole('link', { name: openerName }))
      .first();
    const openable = await opener.isVisible({ timeout: 15_000 }).catch(() => false);
    await capture(page, test.info(), 11, 'tenant-detail');

    if (!openable) {
      blocked(
        'BLOCKED — the console has no way to open the provisioning details of an EXISTING tenant (feature B). '
        + `Execution ${state.executionId} is reachable by API (GET ${CONTRACT.execution(state.executionId!)}) `
        + 'but unreachable by an operator. Set PARTC_PROVISIONING_OPENER to the control that opens it.',
      );
    }

    await opener.click();
    await expect(page).toHaveURL(/tab=provisioning/);
    await expect(page.getByRole('heading', { name: 'Provisioning', exact: true })).toBeVisible();
    await expect(
      page.getByText(`Execution ${state.executionId}`),
      'The Provisioning tab does not name the execution the API returned for this tenant.',
    ).toBeVisible();
    await capture(page, test.info(), 11, 'provisioning-details-open');
  });

  test(step(12, 'the failed step is named on screen with its failure reason'), async () => {
    if (!state.executionId) blocked('Step 11 found no provisioning execution for this tenant.');

    const { status, body } = await json(CONTRACT.execution(state.executionId!));
    expect(status).toBe(200);
    await record(test.info(), 'execution.json', body);
    await capture(page, test.info(), 12, 'failed-step');

    expect(
      body.state,
      `Execution ${state.executionId} is in state "${body.state}", not Failed. PART C certifies recovery of a `
      + 'FAILED attempt; a succeeded or running one is the wrong subject.',
    ).toBe('Failed');
    expect(body.failedStep, 'A Failed execution named no failed step.').toBeTruthy();

    const failed = (body.steps ?? []).find((entry: any) => entry.step === body.failedStep);
    expect(failed, `Execution reports failedStep "${body.failedStep}" but no step row carries it.`).toBeTruthy();
    expect(failed.status).toBe('Failed');
    expect(failed.failureReason, 'The failed step recorded no operator-facing reason.').toBeTruthy();

    // The diagnostics projection must agree with the raw execution. Two endpoints describing the
    // same failure differently is worse than one of them being absent — an operator would act on
    // whichever screen they happened to open.
    const diagnostics = await json(CONTRACT.executionDiagnostics(state.executionId!));
    await record(test.info(), 'execution-diagnostics.json', diagnostics.body);
    if (diagnostics.status === 200) {
      expect(
        diagnostics.body.failedStep?.step ?? diagnostics.body.failedStep?.Step,
        'The diagnostics projection names a different failed step from the execution itself.',
      ).toBe(body.failedStep);
      expect(
        diagnostics.body.classification,
        'The failure carries no classification, so the console cannot tell a customer-input error '
        + 'from a retryable system failure — which is the difference between "fix the address" and "retry".',
      ).toBeTruthy();
    } else {
      partial(
        `execution diagnostics: GET ${CONTRACT.executionDiagnostics(state.executionId!)} answered `
        + `HTTP ${diagnostics.status}, so the failure classification was NOT cross-checked.`,
      );
    }

    // The screen must say the same thing the server does.
    await expect(page.getByText(new RegExp(failed.label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i')).first())
      .toBeVisible();
  });

  test(step(13, 'the activation blockers are visible and the tenant is not activatable'), async () => {
    if (!state.tenantId) blocked('Step 10 did not resolve a tenant.');

    const route = CONTRACT.activationDecision(state.tenantId!);
    const { status, body } = await json(route);
    expect(status, `GET ${route} answered HTTP ${status}.`).toBe(200);
    await record(test.info(), 'activation-decision.json', body);

    expect(
      body.ready,
      'The activation policy says this tenant is ready while its provisioning attempt is Failed. '
      + 'A half-built workspace that passes activation is the defect PART C exists to catch.',
    ).toBe(false);
    expect(body.blockingControls?.length, 'Activation is not ready but names no blocking control.').toBeGreaterThan(0);

    await page.goto(`/platform/tenants/${state.tenantId}?tab=data-storage`);
    await expect(page.getByText(/activation blocked by server policy/i)).toBeVisible();
    for (const code of body.blockingControls) {
      await expect(
        page.getByText(code, { exact: false }).first(),
        `Blocking control "${code}" is enforced by the server but never shown to the operator.`,
      ).toBeVisible();
    }
    await capture(page, test.info(), 13, 'activation-blockers');
  });

  test(step(14, 'governed stale-lease recovery resumes the attempt'), async () => {
    if (!state.executionId) blocked('Step 11 found no provisioning execution for this tenant.');

    // Snapshot the identity surface BEFORE any repair — step 15 compares against this.
    state.identityCountsBefore = await countIdentitySurfaces();
    await record(test.info(), 'identity-counts-before.json', state.identityCountsBefore);

    const probe = await probeApi(
      page.request,
      await bearer(),
      'PARTC_LEASE_RECOVERY_API',
      CONTRACT.leaseRecoveryApi(state.executionId!),
    );
    await record(test.info(), 'lease-recovery-probe.json', probe);
    await capture(page, test.info(), 14, 'recovery-surface');

    // The lease must be VISIBLE before it can be governed. An operator who cannot see who holds a
    // stuck lease is guessing, whatever the recovery endpoint does.
    if (probe.found) {
      expect(
        probe.authorized,
        `${probe.route} exists but answered HTTP ${probe.status} to the Platform Owner.`,
      ).toBe(true);
      await record(test.info(), 'lease-state.json', probe.body);
    }

    // Reached by clicking, not by `goto`. A hard navigation to a code-split platform route races
    // its React.lazy chunk, and a probe that lands on the Suspense spinner reports a rendered
    // panel as missing — a false negative this harness has already been burned by.
    if (!/tab=provisioning/.test(page.url())) {
      await platform.nav(page, 'Tenants').click();
      await typeVisibly(platform.tenantSearch(page), state.tenantName);
      await platform.tenantRow(page, state.tenantName).getByText(state.tenantName, { exact: true }).click();
      await page.getByRole('tab', { name: 'Provisioning', exact: true }).click();
    }
    await settled(page);
    await expect(page.getByRole('heading', { name: 'Readiness', exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Recovery', exact: true })).toBeVisible();

    if (!probe.found) blocked(blockedByMissingApi('the lease assessment (feature C)', probe));

    // ── Is there actually a stale lease? ────────────────────────────────────────────────
    //
    // A lease is RELEASED when a step fails (MarkFailedAsync nulls all four columns), so a
    // normally-failed execution on a healthy single-process stack is `Unowned` and there is
    // nothing to take. Recovering here would be theatre. The harness reports what the server
    // says and moves on to the correction that IS applicable — it does not manufacture a stale
    // lease so that a recovery button has something to press.
    const lease: any = probe.body;
    await record(test.info(), 'lease-assessment.json', lease);
    const staleness = lease?.staleness ?? lease?.Staleness;
    const recoverable = (lease?.isRecoverable ?? lease?.IsRecoverable) === true;

    if (recoverable) {
      const recovery = await page.request.post(absoluteApi(CONTRACT.leaseRecover(state.executionId!)), {
        headers: { Authorization: `Bearer ${state.bearer}`, 'Content-Type': 'application/json' },
        data: { reason: 'PART C certification: governed recovery of a stale provisioning lease.' },
      });
      expect(
        recovery.ok(),
        `Lease recovery answered HTTP ${recovery.status()}: ${await recovery.text()}`,
      ).toBeTruthy();
      state.leaseRecovered = true;
    } else {
      partial(
        `lease recovery: NOT applicable and therefore not performed. The server assessed this execution as `
        + `"${staleness}" with isRecoverable=false — the lease was released when the step failed, so there `
        + 'is no ownership to take. The governed correction exercised below is the RESUME.',
      );
    }

    // ── The correction that does apply: resume the EXISTING execution ───────────────────
    //
    // `step: null` on purpose, never a named step. Naming a step rewinds it to Pending, and the
    // runner deliberately does NOT probe a Pending step — for `invitation` that mints a second
    // live activation link for an account already in use.
    const before: any = (await json(CONTRACT.execution(state.executionId!))).body;
    state.stepsBeforeResume = (before.steps ?? []).map((entry: any) => ({
      step: entry.step,
      status: entry.status,
      attemptCount: entry.attemptCount,
      completedOn: entry.completedOn,
    }));
    await record(test.info(), 'steps-before-resume.json', state.stepsBeforeResume);

    const resume = await page.request.post(absoluteApi(CONTRACT.retry(state.executionId!)), {
      headers: { Authorization: `Bearer ${state.bearer}`, 'Content-Type': 'application/json' },
      data: { step: null, reason: 'PART C certification: root cause repaired; resuming the existing execution.' },
    });
    expect(resume.ok(), `Resume answered HTTP ${resume.status()}: ${await resume.text()}`).toBeTruthy();

    // Watch it actually finish, rather than trusting the 200 on the request that started it.
    await expect
      .poll(async () => (await json(CONTRACT.execution(state.executionId!))).body?.state, { timeout: 120_000 })
      .not.toMatch(/Pending|Running/);

    const after: any = (await json(CONTRACT.execution(state.executionId!))).body;
    await record(test.info(), 'execution-after-resume.json', after);
    await page.getByRole('tab', { name: 'Overview', exact: true }).click();
    await page.getByRole('tab', { name: 'Provisioning', exact: true }).click();
    await settled(page);
    await capture(page, test.info(), 14, 'after-resume');

    expect(
      after.state,
      `The resume left execution ${state.executionId} in "${after.state}". Failure reason: ${after.failureReason}`,
    ).toBe('Succeeded');

    state.recoveryPerformed = true;
    await capture(page, test.info(), 14, 'recovery-performed');
  });

  test(step(15, 'recovery duplicated no tenant, founding user, billing account or entitlement snapshot'), async () => {
    if (!state.identityCountsBefore) {
      blocked('Step 14 never captured a pre-recovery baseline, so "no duplicates" cannot be a measurement.');
    }

    // ── PROOF 1: the resume did not re-run a step that had already committed. ───────────
    //
    // Asserted from the step ROWS, not from the absence of an error. A re-executed step would
    // move its `completedOn` and increment its `attemptCount`; an identical pair on both sides
    // of the resume is positive evidence the runner skipped it.
    const nowSteps: any[] = ((await json(CONTRACT.execution(state.executionId!))).body?.steps ?? []);
    const byCode = new Map(nowSteps.map((entry: any) => [entry.step, entry]));
    const rerun: string[] = [];
    for (const prior of state.stepsBeforeResume ?? []) {
      if (prior.status !== 'Succeeded' && prior.status !== 'Skipped') continue;
      const current = byCode.get(prior.step);
      if (!current) continue;
      if (current.completedOn !== prior.completedOn || current.attemptCount !== prior.attemptCount) {
        rerun.push(
          `${prior.step}: completedOn ${prior.completedOn} → ${current.completedOn}, `
          + `attempts ${prior.attemptCount} → ${current.attemptCount}`,
        );
      }
    }
    await record(test.info(), 'no-rerun-proof.json', {
      before: state.stepsBeforeResume,
      after: nowSteps.map((e: any) => ({ step: e.step, status: e.status, attemptCount: e.attemptCount, completedOn: e.completedOn })),
      rerun,
    });
    expect(
      rerun,
      'The resume RE-RAN a step that had already committed. Every one of these is a duplicate privileged '
      + `write against a customer's workspace: ${rerun.join('; ')}`,
    ).toEqual([]);

    const after = await countIdentitySurfaces();
    await record(test.info(), 'identity-counts-after.json', { before: state.identityCountsBefore, after });
    await capture(page, test.info(), 15, 'no-duplicates');

    // ── PROOF 2: no duplicate identity. ─────────────────────────────────────────────────
    //
    // NOT "every count is unchanged". A resume is supposed to run the steps that had not
    // finished, and the `invitation` step was Pending — so its count going 0 → 1 is the resume
    // WORKING. Demanding stasis there manufactures a defect out of correct behaviour.
    //
    // What must hold is that no identity surface ends up with more than ONE of anything, and
    // that nothing owned by an already-committed step moved at all. The second half is proved
    // above from the step rows; this is the first.
    const AT_MOST_ONE = [
      'tenants-named',
      'distinct-founding-users',
      'distinct-business-units',
      'admin-invitations',
      'billing-accounts',
      'entitlement-snapshots',
    ];

    for (const surface of AT_MOST_ONE) {
      const count = after[surface];
      if (count === undefined) continue;
      if (count < 0) {
        partial(`${surface}: no platform read surface on this build, so duplication there was NOT measured.`);
        continue;
      }
      expect(
        count,
        `${surface} ended at ${count} (was ${state.identityCountsBefore![surface]} before the resume). `
        + 'Governed recovery must never leave a customer with two of anything.',
      ).toBeLessThanOrEqual(1);
    }

    expect(after['tenants-named'], `More than one tenant is now named "${state.tenantName}".`).toBe(1);
  });

  test(step(16, 'production-only dependencies remain visibly pending'), async () => {
    if (!state.tenantId) blocked('Step 10 did not resolve a tenant.');

    const probe = await probeApi(
      page.request,
      await bearer(),
      'PARTC_DEPLOYMENT_PROFILE_API',
      CONTRACT.deploymentProfileApi(state.tenantId!),
    );
    await record(test.info(), 'deployment-profile-probe.json', probe);
    await capture(page, test.info(), 16, 'deployment-profile');

    if (!probe.found) {
      blocked(
        blockedByMissingApi('the tenant deployment profile / production-only dependencies (feature B)', probe)
        + ' The backend has no DeploymentProfile, DeploymentMode or production-only-dependency concept on this '
        + 'branch; the nearest neighbours are Tenant.DataRegion and the integrations.mandatory activation control. '
        + 'Land the projection, or set PARTC_DEPLOYMENT_PROFILE_API.',
      );
    }

    const payload: any = probe.body;
    expect(
      payload.deploymentProfile ?? payload.DeploymentProfile,
      'The projection records no deployment profile, so a decision taken on a laptop is indistinguishable '
      + 'from one taken on production.',
    ).toBeTruthy();

    // The honest list. A prerequisite DEFERRED under a non-production profile must still read as
    // a production blocker — a deferral that disappears from the screen is a deferral nobody
    // remembers before go-live.
    const productionBlockers: any[] = payload.productionBlockers ?? payload.ProductionBlockers
      ?? payload.productionBlockingControls ?? payload.ProductionBlockingControls ?? [];
    await record(test.info(), 'production-blockers.json', { profile: payload.deploymentProfile, productionBlockers });

    expect(
      productionBlockers.length,
      'A tenant whose provisioning failed reports ZERO production blockers. Either the projection is not '
      + 'listing deferred prerequisites, or a half-built workspace is being certified production-ready.',
    ).toBeGreaterThan(0);

    for (const blocker of productionBlockers) {
      expect(
        blocker.blocksProduction ?? blocker.BlocksProduction ?? true,
        `Blocker ${JSON.stringify(blocker)} is deferred AND no longer marked as blocking production. `
        + 'That is the false-green PART C exists to prevent.',
      ).toBe(true);
    }

    if (!/tab=provisioning/.test(page.url())) {
      await platform.nav(page, 'Tenants').click();
      await typeVisibly(platform.tenantSearch(page), state.tenantName);
      await platform.tenantRow(page, state.tenantName).getByText(state.tenantName, { exact: true }).click();
      await page.getByRole('tab', { name: 'Provisioning', exact: true }).click();
    }
    await settled(page);
    // Matched as TEXT, not as a heading: BlockerList renders its title in a plain <Typography>
    // (a <p>), so there is no heading role to target. Noted as a minor a11y observation — two
    // parallel blocker lists whose titles are not headings are hard to navigate non-visually.
    const shown = page.getByText('Production blockers', { exact: true });
    if (!(await shown.isVisible({ timeout: 20_000 }).catch(() => false))) {
      await capture(page, test.info(), 16, 'production-dependencies-not-shown');
      partial(
        'production-only dependencies: the server projects them but the Readiness panel showed no '
        + '"Production blockers" list. The API half of this step passed; the operator-visible half was not.',
      );
      return;
    }

    // A deferral must never empty this list. That is the whole contract of a non-production profile:
    // it defers a prerequisite for ACTIVATION, and still owes it before go-live.
    await expect(page.getByText(/Deployment profile:/)).toBeVisible();
    for (const blocker of productionBlockers) {
      const code = blocker.code ?? blocker.Code ?? blocker.control ?? blocker.controlCode;
      if (!code) continue;
      await expect(
        page.getByText(String(code), { exact: false }).first(),
        `Production blocker "${code}" is projected by the server but never shown to the operator.`,
      ).toBeVisible();
    }
    await capture(page, test.info(), 16, 'production-dependencies-pending');
  });
});

// ═══════════════════════════════════════════════════════════════════════════
// SECTION 3 — restoring enforcement and proving the audit trail (steps 17-18)
// ═══════════════════════════════════════════════════════════════════════════

test.describe.serial('PART C · Section 3 — enforcement restored and audited', () => {
  test(step(17, 'REQUIRED is re-enabled and enforcement returns WITHOUT re-enrollment'), async () => {
    if (!state.policyRelaxed || !state.policyRoute) {
      blocked('The policy was never relaxed (see steps 3-5), so there is nothing to restore.');
    }

    await openViaSidebar(page, 'Platform Authentication', state.policyRoute!);
    const phrase = state.policyPhrases?.REQUIRED;
    expect(phrase, 'The server served no confirmation phrase for REQUIRED; the harness will not guess it.').toBeTruthy();

    await page.getByLabel(POLICY_CONTROLS.modeSelect).selectOption('REQUIRED');
    await typeVisibly(
      page.getByRole('textbox', { name: POLICY_CONTROLS.reasonField, exact: true }),
      'PART C certification complete: restoring enforced multi-factor authentication.',
    );
    await typeVisibly(page.getByLabel(POLICY_CONTROLS.confirmationField), phrase!);
    await typeVisibly(page.getByLabel(POLICY_CONTROLS.currentPasswordField), resolveCredentials().password);
    await page.getByRole('button', { name: POLICY_CONTROLS.applyButton }).click();

    // Applying REQUIRED de-privileges THIS session in the same instant: it holds no `amr=mfa`,
    // so the Owner-only policy read it would use to confirm the write is now 403 to it. Verifying
    // here would be asking a session to prove something it has just lost the right to see.
    // The confirmation therefore happens after signing in again, below — which is also exactly
    // what the journey is asking about.
    await capture(page, test.info(), 17, 'immediately-after-restoring-required');

    // What the operator is looking at right now is itself a finding. An already-enrolled Owner
    // must not be told to enrol.
    const enrolmentGate = page.getByRole('heading', { name: /set up multi-factor authentication/i });
    if (await enrolmentGate.isVisible({ timeout: 10_000 }).catch(() => false)) {
      partial(
        'After restoring REQUIRED, the console shows the already-enrolled Owner the ENROLLMENT gate '
        + '("Set up multi-factor authentication … Enrol an authenticator below to continue"). The '
        + 'authenticator seed is intact — the sign-in below proves it — so the copy instructs an '
        + 'operator to redo something that would invalidate the factor they still hold.',
      );
      await capture(page, test.info(), 17, 'enrolment-gate-shown-to-enrolled-owner');
    }

    // The enrolment gate replaces the console chrome, so its own "Sign out" button is the way out.
    const gateSignOut = page.getByRole('button', { name: 'Sign out', exact: true });
    if (await gateSignOut.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await gateSignOut.click();
      await expect(platform.loginHeading(page)).toBeVisible({ timeout: 20_000 });
    } else {
      await signOutOfPlatformConsole(page);
    }

    const outcome = await signInAsPlatformOwner(page, resolveCredentials());
    await capture(page, test.info(), 17, 'enforcement-returned');

    // Confirmed with the session that CAN read it — one that presented a second factor.
    await bearer();
    await expect
      .poll(
        async () => (await json(CONTRACT.mfaPolicyApi()[0])).body?.mode,
        { message: 'The server did not record REQUIRED after the restore.', timeout: 20_000 },
      )
      .toBe('REQUIRED');
    const restored = await probeApi(page.request, await bearer(), 'PARTC_MFA_POLICY_API', CONTRACT.mfaPolicyApi());
    await record(test.info(), 'mfa-policy-restored.json', restored.body);
    expect((restored.body as any)?.enforcementDisabled, 'Enforcement is still disabled after the restore.').toBe(false);
    await expect(
      page.getByTestId(POLICY_CONTROLS.bannerTestId),
      'Enforcement is REQUIRED again but the disabled-MFA banner is still on screen.',
    ).toHaveCount(0);

    expect(
      outcome.enrollmentDemanded,
      'Restoring REQUIRED pushed the Owner into MFA ENROLLMENT. The policy round-trip destroyed the enrolled '
      + 'authenticator seed — every operator would have to re-enrol, and the recovery codes issued at '
      + 'enrollment are now the only way back in.',
    ).toBe(false);
    expect(
      outcome.authenticatorChallenged,
      'REQUIRED is recorded but the console let the Owner in without a second factor. The policy is decorative.',
    ).toBe(true);

    state.policyRelaxed = false;
    state.policyRestored = true;
    await bearer();
  });

  test(step(18, 'the audit trail carries both policy changes and the recovery, with actor and reason'), async () => {
    // Re-establish the session if an earlier section ended without one. Section 3 must be able to
    // report on the audit trail even when the journey fell over before reaching it.
    if (!(await page.evaluate(() => sessionStorage.getItem('nexora_platform_token')).catch(() => null))) {
      await signInAsPlatformOwner(page, resolveCredentials());
    }
    await bearer();

    const { status, body } = await json(CONTRACT.audit);
    expect(status, `GET ${CONTRACT.audit} answered HTTP ${status}.`).toBe(200);
    const entries: any[] = Array.isArray(body) ? body : [];
    await record(test.info(), 'audit-entries.json', entries.slice(0, 80));

    await page.goto('/platform/audit');
    await expect(page.getByRole('heading', { name: 'Audit Log', exact: true })).toBeVisible();
    await capture(page, test.info(), 18, 'audit-log');

    const expectations: Array<{ what: string; match: (entry: any) => boolean; required: boolean }> = [
      {
        what: 'the relaxation to DISABLED_TEST_ONLY',
        match: (entry) => /DISABLED[_\s-]?TEST[_\s-]?ONLY/i.test(JSON.stringify(entry)),
        // Gated on the change having HAPPENED, not on the endpoint existing. Requiring an audit
        // row for a change that was never made manufactures a defect against another engineer.
        required: state.policyEverRelaxed,
      },
      {
        what: 'the restoration to REQUIRED',
        match: (entry) =>
          /mfa|authentication|policy/i.test(String(entry.action ?? '')) && /REQUIRED/i.test(JSON.stringify(entry)),
        required: state.policyRestored,
      },
      {
        what: 'the governed provisioning recovery',
        match: (entry) => /provisioning\.(retry|recover|lease)/i.test(String(entry.action ?? '')),
        required: state.recoveryPerformed,
      },
    ];

    const unaudited = expectations
      .filter((expectation) => expectation.required && !entries.some(expectation.match))
      .map((expectation) => expectation.what);

    const nothingWasRequired = expectations.every((expectation) => !expectation.required);
    if (nothingWasRequired) {
      blocked(
        'Neither the policy change nor the recovery ran (features A and C are absent), so there is no PART C '
        + 'audit history to verify. The audit endpoint itself answered 200 and the Audit Log screen rendered.',
      );
    }

    expect(
      unaudited,
      `Privileged actions were performed and left no audit entry: ${unaudited.join(', ')}. An action that cannot `
      + 'be reconstructed afterwards was not governed, whatever the screen said at the time.',
    ).toEqual([]);

    // An audit row without an actor and a reason is a log line, not evidence.
    const partCEntries = entries.filter((entry) => /PART C certification/i.test(JSON.stringify(entry)));
    for (const entry of partCEntries) {
      expect(entry.actor || entry.actorEmail, `Audit entry ${entry.id} names no actor.`).toBeTruthy();
    }
    await record(test.info(), 'partc-audit-entries.json', partCEntries);
  });
});

// ───────────────────────────────────────────────────────────────────────────
// Identity surfaces counted before and after recovery.
//
// -1 means "this build exposes no platform read surface for it" and is reported as an
// unmeasured sub-check rather than quietly counted as zero.
// ───────────────────────────────────────────────────────────────────────────

async function countIdentitySurfaces(): Promise<Record<string, number>> {
  if (!state.tenantId) throw new Error('Identity surfaces cannot be counted before step 10 resolves the tenant.');
  const counts: Record<string, number> = {};

  const tenants = await json(CONTRACT.tenants);
  counts['tenants-named'] = Array.isArray(tenants.body)
    ? tenants.body.filter((tenant: any) => tenant.name?.trim() === state.tenantName).length
    : -1;

  const executions = await json(CONTRACT.executionsList);
  const mine: any[] = Array.isArray(executions.body)
    ? executions.body.filter((execution: any) => String(execution.tenantId ?? '') === state.tenantId)
    : [];
  counts['distinct-founding-users'] = new Set(mine.map((e) => e.foundingUserId).filter(Boolean)).size;
  counts['distinct-business-units'] = new Set(mine.map((e) => e.provisionedBusinessUnitId).filter(Boolean)).size;

  const invitations = await json(CONTRACT.invitations(state.tenantId!));
  counts['admin-invitations'] = Array.isArray(invitations.body) ? invitations.body.length : -1;

  const billing = await json(CONTRACT.tenantBilling(state.tenantId!));
  counts['billing-accounts'] = billing.status === 200 ? (Array.isArray(billing.body) ? billing.body.length : 1) : -1;

  const snapshot = await probeApi(
    page.request,
    state.bearer ?? (await bearer()),
    'PARTC_ENTITLEMENT_SNAPSHOT_API',
    CONTRACT.entitlementSnapshotApi(state.tenantId!),
  );
  counts['entitlement-snapshots'] = snapshot.authorized
    ? Array.isArray(snapshot.body)
      ? snapshot.body.length
      : 1
    : -1;

  return counts;
}

/** Keeps the ApiProbe type referenced so the contract stays honest under `noUnusedLocals`. */
export type PartCProbe = ApiProbe;
