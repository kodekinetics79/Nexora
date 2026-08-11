/**
 * PART C harness support — reusable verification assets for the governed
 * stale-lease recovery / platform MFA policy certification.
 *
 * TEST-ONLY. Nothing here is imported by `src/`.
 *
 * DESIGN NOTES THAT MATTER FOR REVIEW
 *
 * 1. No `node:` imports. `Frontend/tsconfig.json` has no `@types/node` available in this
 *    workspace, so a spec that reaches for `node:crypto` cannot be type-checked at all —
 *    which is how `Frontend/e2e` came to contain 35 spec files that `tsc` has never seen.
 *    TOTP is therefore derived with Web Crypto (`crypto.subtle`, global since Node 19),
 *    and the authenticator seed arrives as an environment value rather than being read
 *    off disk by the spec.
 *
 * 2. Capability probing, not route guessing. Three features are being written concurrently
 *    by other agents and their real routes are not knowable from this branch. Every probe
 *    takes an ORDERED CANDIDATE LIST, records every candidate it tried and the status each
 *    returned, and is overridable by a single environment variable. When a probe finds
 *    nothing the harness skips with the full list in the message — that list is the exact
 *    request to the implementing agent, not a guess about what they built.
 *
 * 3. A probe that answers 401/403 is a FINDING, not an absence. A route that exists but
 *    refuses the Platform Owner is reported as `found: true, authorized: false` so it can
 *    never be mistaken for "the feature has not landed yet".
 */

import { expect, type APIRequestContext, type Locator, type Page, type TestInfo } from '@playwright/test';

// ---------------------------------------------------------------------------
// Environment
// ---------------------------------------------------------------------------

/**
 * Asserts named environment values are present and returns them typed.
 *
 * Deliberately called from INSIDE a test body, never at module scope: Playwright evaluates
 * every spec file to collect its tests, so a module-scope throw aborts discovery for the
 * whole directory and `--list` reports zero tests.
 */
export const requirePartCEnv = <K extends string>(context: string, ...names: K[]): Record<K, string> => {
  const missing = names.filter((name) => !process.env[name]?.trim());
  if (missing.length > 0) {
    throw new Error(
      `${context} requires these environment values, and none of them may be invented by the harness: ${missing.join(', ')}.`,
    );
  }
  return Object.fromEntries(names.map((name) => [name, process.env[name]!.trim()])) as Record<K, string>;
};

/** An env override wins outright; otherwise the built-in candidate list is used in order. */
export const candidates = (variable: string, fallback: string[]): string[] => {
  const override = process.env[variable]?.trim();
  if (override) return override.split(',').map((entry) => entry.trim()).filter(Boolean);
  return fallback;
};

export const apiUrl = (): string => {
  const value = process.env.E2E_API_URL?.trim();
  if (!value) throw new Error('E2E_API_URL must name the real backend, e.g. http://127.0.0.1:5192.');
  return value.replace(/\/$/, '');
};

export const absoluteApi = (path: string): string => new URL(path, `${apiUrl()}/`).toString();

// ---------------------------------------------------------------------------
// TOTP — the real second factor, derived the way an authenticator app derives it
// ---------------------------------------------------------------------------

const BASE32 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

/**
 * Returns an `ArrayBuffer` rather than a `Uint8Array` on purpose: `Uint8Array` is generic over
 * its backing buffer in TypeScript 5.7+, and the inferred `Uint8Array<ArrayBufferLike>` is not
 * assignable to `BufferSource` (it might be shared memory). Allocating the buffer here keeps
 * `crypto.subtle.importKey` type-safe without a cast.
 */
export const decodeBase32 = (value: string): ArrayBuffer => {
  const bits = value
    .toUpperCase()
    .replace(/[^A-Z2-7]/g, '')
    .split('')
    .map((character) => BASE32.indexOf(character).toString(2).padStart(5, '0'))
    .join('');
  const chunks = bits.match(/.{8}/g) ?? [];
  const buffer = new ArrayBuffer(chunks.length);
  const view = new Uint8Array(buffer);
  chunks.forEach((byte, index) => {
    view[index] = Number.parseInt(byte, 2);
  });
  return buffer;
};

/** RFC 6238 TOTP, SHA-1, 30-second step, 6 digits — what the backend's enrollment issues. */
export const currentTotp = async (secret: string, now = Date.now()): Promise<string> => {
  const key = await crypto.subtle.importKey(
    'raw',
    decodeBase32(secret),
    { name: 'HMAC', hash: 'SHA-1' },
    false,
    ['sign'],
  );
  const counter = new ArrayBuffer(8);
  new DataView(counter).setBigUint64(0, BigInt(Math.floor(now / 30_000)));
  const signature = new Uint8Array(await crypto.subtle.sign('HMAC', key, counter));
  const offset = signature[signature.length - 1] & 0x0f;
  const truncated =
    ((signature[offset] & 0x7f) << 24)
    | (signature[offset + 1] << 16)
    | (signature[offset + 2] << 8)
    | signature[offset + 3];
  return String(truncated % 1_000_000).padStart(6, '0');
};

/**
 * The server fences replay of a TOTP time step. This journey signs in three times in a few
 * minutes, so it waits VISIBLY for the next genuine 30-second window rather than weakening
 * that control or stubbing the clock.
 *
 * The process-start step is treated as already spent: an earlier interrupted run may have
 * submitted it and the server is right to remember.
 */
const lastSubmittedStep = new Map<string, number>();

export const nextUnusedTotp = async (page: Page, secret: string): Promise<string> => {
  let now = Date.now();
  let step = Math.floor(now / 30_000);
  const spent = lastSubmittedStep.get(secret) ?? Math.floor(Date.now() / 30_000);
  if (step <= spent) {
    await page.waitForTimeout((spent + 1) * 30_000 - now + 500);
    now = Date.now();
    step = Math.floor(now / 30_000);
  }
  lastSubmittedStep.set(secret, step);
  return currentTotp(secret, now);
};

// ---------------------------------------------------------------------------
// Real selectors, verified against src/platform on this branch
// ---------------------------------------------------------------------------

export const platform = {
  loginHeading: (page: Page): Locator => page.getByRole('heading', { name: 'Platform Console' }),
  emailField: (page: Page): Locator => page.getByRole('textbox', { name: 'Email' }),
  passwordField: (page: Page): Locator => page.getByLabel('Password'),
  submitCredentials: (page: Page): Locator => page.getByRole('button', { name: 'Enter Control Plane' }),
  authenticatorField: (page: Page): Locator => page.getByLabel('6-digit authenticator code'),
  verifyAndEnter: (page: Page): Locator => page.getByRole('button', { name: 'Verify and enter' }),
  overviewHeading: (page: Page): Locator => page.getByRole('heading', { name: 'Platform Overview' }),
  // MUI Tooltip labels an icon-only child with its title string.
  signOut: (page: Page): Locator => page.getByRole('button', { name: 'Sign out of platform console' }),
  nav: (page: Page, label: string): Locator => page.getByRole('link', { name: label, exact: true }),
  /**
   * By PLACEHOLDER, not by label.
   *
   * TenantsPage puts `aria-label="Search tenants"` directly on the MUI `TextField`, which forwards
   * unknown props to its ROOT `<div>` rather than to the `<input>` (reaching the input needs
   * `slotProps.htmlInput`). So the accessible name "Search tenants" belongs to a generic div that
   * cannot be typed into, and the input itself is named only by its placeholder. Reported as a
   * minor a11y defect; the harness drives the real control in the meantime.
   */
  tenantSearch: (page: Page): Locator =>
    page.getByPlaceholder('Search name, slug, legal name or contact…'),
  tenantRow: (page: Page, name: string): Locator => page.getByRole('row').filter({ hasText: name }),
};

/** Types into a field the way an operator does, so `onChange` handlers actually run. */
export const typeVisibly = async (field: Locator, value: string): Promise<void> => {
  await field.click();
  // Cleared through the keyboard rather than `fill('')`: `fill` refuses any element that is not
  // a real input, which is exactly the failure mode a mislabelled MUI wrapper produces, and it
  // also skips the change handlers a controlled React input depends on.
  await field.press('ControlOrMeta+a');
  await field.press('Backspace');
  await field.pressSequentially(value);
};

export interface PlatformOwnerCredentials {
  email: string;
  password: string;
  totpSecret: string;
}

export interface SignInOutcome {
  /** True when the server demanded the authenticator — i.e. MFA is being ENFORCED. */
  authenticatorChallenged: boolean;
  /** True when the server tried to make this account ENROLL rather than verify. */
  enrollmentDemanded: boolean;
}

/**
 * Signs in through the real screen and reports which factors the server actually demanded.
 *
 * The return value is the assertion surface for PART C: "logged back in with no authenticator
 * challenge" and "enforcement returned without re-enrollment" are both statements about what
 * the server asked for, so this helper reports it instead of hiding it.
 */
export const signInAsPlatformOwner = async (
  page: Page,
  credentials: PlatformOwnerCredentials,
): Promise<SignInOutcome> => {
  await page.goto('/platform/overview');
  await expect(platform.loginHeading(page)).toBeVisible({ timeout: 30_000 });

  await typeVisibly(platform.emailField(page), credentials.email);
  await typeVisibly(platform.passwordField(page), credentials.password);
  await platform.submitCredentials(page).click();

  const authenticator = platform.authenticatorField(page);
  const overview = platform.overviewHeading(page);
  // Enrollment is a DIFFERENT screen from verification, and conflating them is exactly the
  // defect PART C step 17 exists to catch: a policy round-trip that silently wipes the
  // enrolled seed would present a setup key, not a code box.
  const enrollment = page.getByText(/setup key|authenticator uri|scan this|enroll/i).first();

  // `.first()` on the whole alternation, not on one branch: `a.or(b)` matches BOTH when both are
  // present, and `toBeVisible` is strict, so racing three outcomes without it fails with a strict
  // mode violation the moment two of them are legitimately on screen at once.
  await expect(authenticator.or(overview).or(enrollment).first()).toBeVisible({ timeout: 30_000 });

  const enrollmentDemanded = (await enrollment.isVisible()) && !(await authenticator.isVisible());
  const authenticatorChallenged = await authenticator.isVisible();

  if (authenticatorChallenged) {
    await authenticator.fill(await nextUnusedTotp(page, credentials.totpSecret));
    await platform.verifyAndEnter(page).click();
  }

  if (!enrollmentDemanded) {
    // Asserted separately rather than as an alternation: both are expected, and "either one
    // showed up" is a weaker claim than the one this step is making.
    await expect(overview).toBeVisible({ timeout: 30_000 });
    await expect(
      page.getByText(/scope=platform/i).first(),
      'The console did not show the platform scope badge, so this session may not be platform-scoped.',
    ).toBeVisible();
  }

  return { authenticatorChallenged, enrollmentDemanded };
};

export const signOutOfPlatformConsole = async (page: Page): Promise<void> => {
  await platform.signOut(page).click();
  await expect(platform.loginHeading(page)).toBeVisible({ timeout: 20_000 });
};

/**
 * The live platform bearer, read from the session the browser is actually holding.
 *
 * `sessionStorage` is origin-scoped, so reading it while the page sits on `about:blank` — which is
 * where a context lands after an earlier step failed before navigating — throws a SecurityError
 * that looks like an auth defect and is not one. Navigate first, then read.
 */
export const platformBearer = async (page: Page): Promise<string> => {
  if (!/^https?:/.test(page.url())) await page.goto('/platform/overview');
  return page.evaluate(() => {
    const token = sessionStorage.getItem('nexora_platform_token');
    if (!token) throw new Error('The authenticated platform session held no bearer token.');
    return token;
  });
};

// ---------------------------------------------------------------------------
// Capability probing
// ---------------------------------------------------------------------------

export interface CandidateAttempt {
  route: string;
  status: number | 'network-error';
}

export interface ApiProbe {
  /** A candidate answered with something other than 404/405 — the route exists. */
  found: boolean;
  /** The route answered 2xx for the Platform Owner. */
  authorized: boolean;
  route: string | null;
  status: number | null;
  body: unknown;
  attempts: CandidateAttempt[];
  variable: string;
}

/**
 * Tries each candidate route in order and returns the first that is not "absent".
 *
 * 404 and 405 mean absent. ANY other status means the route exists — 401/403 in particular
 * are reported as `found: true, authorized: false`, because "the Owner is forbidden from the
 * platform authentication policy" is a defect to raise, not a feature to wait for.
 */
export const probeApi = async (
  request: APIRequestContext,
  token: string,
  variable: string,
  routes: string[],
  init: { method?: 'GET' | 'POST'; data?: unknown } = {},
): Promise<ApiProbe> => {
  const attempts: CandidateAttempt[] = [];
  for (const route of routes) {
    let response;
    try {
      response = await request.fetch(absoluteApi(route), {
        method: init.method ?? 'GET',
        headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
        ...(init.data === undefined ? {} : { data: init.data }),
      });
    } catch {
      attempts.push({ route, status: 'network-error' });
      continue;
    }
    const status = response.status();
    attempts.push({ route, status });
    if (status === 404 || status === 405) continue;

    let body: unknown = null;
    try {
      body = await response.json();
    } catch {
      body = await response.text().catch(() => null);
    }
    return { found: true, authorized: response.ok(), route, status, body, attempts, variable };
  }
  return { found: false, authorized: false, route: null, status: null, body: null, attempts, variable };
};

/** The message a blocked step reports. It names every candidate and what each answered. */
export const blockedByMissingApi = (feature: string, probe: ApiProbe): string =>
  [
    `BLOCKED — ${feature} is not reachable on this build.`,
    `Tried, in order: ${probe.attempts.map((a) => `${a.route} → ${a.status}`).join('; ') || '(no candidates configured)'}.`,
    `Set ${probe.variable}=<real route> (comma-separated for several) to point the harness at the shipped route,`,
    'or land the endpoint. This step asserts nothing and must not be read as a pass.',
  ].join(' ');

export const blockedByMissingUi = (feature: string, tried: string[], variable: string): string =>
  [
    `BLOCKED — ${feature} has no reachable surface in the console.`,
    `Tried: ${tried.join('; ') || '(no candidates configured)'}.`,
    `Set ${variable} to the real route, or land the screen.`,
    'This step asserts nothing and must not be read as a pass.',
  ].join(' ');

/**
 * Visits each candidate console route and returns the first where `detect` becomes visible.
 * Records what was tried either way.
 *
 * The timeout is generous, and that is not padding. Every platform page is `React.lazy`, and the
 * console runs against a VITE DEV SERVER that transpiles a route's chunk the first time anyone
 * asks for it. A short probe screenshots `PlatformLoader`'s spinner and reports "this screen does
 * not exist" about a screen that does — a false BLOCKED verdict against another engineer's work,
 * which is the most expensive kind of wrong this harness can be.
 */
export const probeConsoleRoute = async (
  page: Page,
  routes: string[],
  detect: (page: Page) => Locator,
): Promise<{ route: string | null; tried: string[] }> => {
  const timeout = Number(process.env.PARTC_ROUTE_PROBE_TIMEOUT_MS ?? '20000');
  const tried: string[] = [];

  // The page the caller is ALREADY on counts, and is checked first without navigating. Step 3
  // reaches this screen by clicking Security and then its link — an operator's path — and a probe
  // that immediately hard-navigated away would throw that evidence out and re-test something else.
  if (await detect(page).first().isVisible({ timeout }).catch(() => false)) {
    tried.push(`${page.url()} (already open) → found`);
    return { route: page.url(), tried };
  }
  tried.push(`${page.url()} (already open) → not found within ${timeout}ms`);

  for (const route of routes) {
    await page.goto(route);
    // Back-to-back `goto` calls abort each other's in-flight module requests, and every platform
    // page is `React.lazy`: a cancelled chunk leaves Suspense on its spinner and the probe
    // concludes a shipped screen is missing. Let the document settle before deciding.
    await page.waitForLoadState('networkidle').catch(() => undefined);
    const visible = await detect(page).first().isVisible({ timeout }).catch(() => false);
    tried.push(`${route} → ${visible ? 'found' : `not found within ${timeout}ms`}`);
    if (visible) return { route, tried };
  }
  return { route: null, tried };
};

/**
 * Waits for the console to stop suspending.
 *
 * Every platform page is `React.lazy`, so a screenshot or a locator taken while a chunk is still
 * resolving sees `PlatformLoader`'s spinner and nothing else — which reads as "the panel is not
 * there". Waiting for the spinner to clear turns that class of false negative into a real read.
 */
export const settled = async (page: Page): Promise<void> => {
  await page.waitForLoadState('networkidle').catch(() => undefined);
  await expect(page.locator('.MuiCircularProgress-root')).toHaveCount(0, { timeout: 30_000 }).catch(() => undefined);
};

/**
 * Opens a platform screen the way an operator does — by clicking the sidebar — falling back to a
 * URL only if the sidebar has no such entry. Used instead of `page.goto` for the screens this
 * journey returns to, so navigation is never the thing under suspicion.
 */
export const openViaSidebar = async (page: Page, label: string, fallbackUrl: string): Promise<void> => {
  const entry = platform.nav(page, label);
  if (await entry.isVisible({ timeout: 5_000 }).catch(() => false)) {
    await entry.click();
    return;
  }
  await page.goto(fallbackUrl);
  await page.waitForLoadState('networkidle').catch(() => undefined);
};

// ---------------------------------------------------------------------------
// Evidence
// ---------------------------------------------------------------------------

/**
 * Every step captures a screenshot, whether it passed, blocked or is about to fail.
 *
 * Ordinal-prefixed into a stable directory so the 18 files read as the journey in order —
 * a certification pack a human can scroll, not an archive keyed by test-name hashes.
 */
export const evidenceDir = (): string =>
  (process.env.PARTC_EVIDENCE_DIR
    ?? `${process.env.PARTC_ARTIFACT_ROOT || '../.local-run/partc'}/evidence`).replace(/\/$/, '');

export const capture = async (
  page: Page,
  testInfo: TestInfo,
  ordinal: number,
  slug: string,
): Promise<string> => {
  const name = `${String(ordinal).padStart(2, '0')}-${slug}.png`;
  const path = `${evidenceDir()}/${name}`;
  await page.screenshot({ path, fullPage: true });
  await testInfo.attach(name, { path, contentType: 'image/png' });
  return path;
};

/** Attaches machine-readable evidence beside the screenshot. */
export const record = async (testInfo: TestInfo, name: string, value: unknown): Promise<void> => {
  await testInfo.attach(name, {
    body: JSON.stringify(value, null, 2),
    contentType: 'application/json',
  });
};

// ---------------------------------------------------------------------------
// Shared journey state
//
// The 18 steps are separate tests so each one reports its own verdict, but they are one
// journey against one session. What one step learns, the next needs.
// ---------------------------------------------------------------------------

export interface JourneyState {
  tenantName: string;
  tenantId: string | null;
  executionId: string | null;
  bearer: string | null;
  baselineChallenged: boolean | null;
  policyRelaxed: boolean;
  /** Sticky: the relaxation HAPPENED at some point, even after step 17 put REQUIRED back. */
  policyEverRelaxed: boolean;
  policyRestored: boolean;
  policyRoute: string | null;
  policyApiRoute: string | null;
  /** Confirmation phrases as SERVED by the API. Never hard-coded, so a phrase change is caught. */
  policyPhrases: Record<string, string> | null;
  recoveryPerformed: boolean;
  /** True only when a genuinely stale lease was found and taken over. */
  leaseRecovered: boolean;
  /** Step verdicts captured immediately BEFORE the resume — the no-re-run proof's baseline. */
  stepsBeforeResume: Array<{ step: string; status: string; attemptCount: number; completedOn: string | null }> | null;
  identityCountsBefore: Record<string, number> | null;
}

export const newJourneyState = (): JourneyState => ({
  tenantName: process.env.PARTC_TENANT_NAME?.trim() || 'Noor and Sons',
  tenantId: null,
  executionId: null,
  bearer: null,
  baselineChallenged: null,
  policyRelaxed: false,
  policyEverRelaxed: false,
  policyRestored: false,
  policyRoute: null,
  policyApiRoute: null,
  policyPhrases: null,
  recoveryPerformed: false,
  leaseRecovered: false,
  stepsBeforeResume: null,
  identityCountsBefore: null,
});
