import { expect, test, type Page } from '@playwright/test';

const measuredAt = '2026-07-30T18:00:00Z';
const workload = {
  activeLeadCount: 3,
  leadLineCount: 12,
  overdueDeadlineCount: 1,
  urgentDeadlineCount: 1,
  approachingDeadlineCount: 0,
  openRfqCount: 2,
  openQuoteCount: 1,
  followUpCount: 2,
  workloadPoints: 37,
};
const owners = [
  { userId: 101, name: 'Avery Recommended', email: 'avery@nexora.invalid', roleName: 'Sales Representative', isAvailable: true, capacityPercent: 80, workload, hasGovernedProfile: true, eligibilityReason: 'Eligible governed profile.', measuredAtUtc: measuredAt, policyVersion: 'routing-v1' },
  { userId: 102, name: 'Blair Alternate', email: 'blair@nexora.invalid', roleName: 'Sales Representative', isAvailable: true, capacityPercent: 60, workload: { ...workload, workloadPoints: 21 }, hasGovernedProfile: true, eligibilityReason: 'Eligible governed profile.', measuredAtUtc: measuredAt, policyVersion: 'routing-v1' },
];

async function authorizeManager(page: Page, modules = ['Leads', 'Customers', 'Users', 'Dashboard', 'Quotations']) {
  await page.addInitScript((authorizedModules) => {
    const current = JSON.parse(localStorage.getItem('userData') ?? '{}');
    const permissions = authorizedModules.map((moduleName, index) => ({
      id: 9300 + index,
      moduleId: 9300 + index,
      moduleName,
      roleId: current.roleId ?? 1,
      canCreate: true,
      canEdit: true,
      canDelete: false,
    }));
    localStorage.setItem('userData', JSON.stringify({ ...current, isManager: true, isSuperAdmin: false, permissions }));
  }, modules);
}

async function mockOwnerOptions(page: Page) {
  await page.route(/\/api\/commercial-intelligence\/(routing|account)-owner-options$/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(owners),
  }));
}

test('manager accepts an explainable routing recommendation through visible controls', async ({ page }) => {
  await authorizeManager(page);
  await mockOwnerOptions(page);
  await page.route('**/api/commercial-intelligence/routing-queue*', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{
      sourceId: 501,
      leadId: 601,
      nexoraSerial: 'NX-2026-000601',
      customerName: 'Atlas Controls',
      receivedAt: '2026-07-30T16:00:00Z',
      dueAt: '2026-07-30T17:00:00Z',
      reason: 'Confirm a sales owner',
      recommendedOwnerUserId: 101,
      recommendedOwnerName: 'Avery Recommended',
      recommendationReason: 'LOWEST_VERIFIED_WORKLOAD',
      matchConfidence: 0.91,
      policyVersion: 'routing-v1',
      recommendationMeasuredAt: measuredAt,
      recommendedOwnerAvailable: true,
      recommendedOwnerCapacityPercent: 80,
      recommendedOwnerWorkloadPoints: 37,
      priority: 90,
      status: 'Open',
      overdue: true,
      version: 4,
    }]),
  }));
  let assignment: Record<string, unknown> | null = null;
  const idempotencyKeys: string[] = [];
  await page.route('**/api/commercial-intelligence/routing-queue/501/assign', async route => {
    idempotencyKeys.push(route.request().headers()['idempotency-key']);
    assignment = route.request().postDataJSON();
    await route.fulfill(idempotencyKeys.length === 1
      ? { status: 503, contentType: 'application/json', body: '{"error":"Temporary network failure"}' }
      : { status: 200, contentType: 'application/json', body: '{}' });
  });

  await page.goto('/sales/routing');
  await expect(page.getByRole('heading', { name: 'Routing queue' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Lead routing queue' })).toContainText('91% match');
  await expect(page.getByRole('region', { name: 'Lead routing queue' })).toContainText('37 workload points');
  await page.getByRole('button', { name: 'Assign', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Assign NX-2026-000601' })).toContainText('Policy routing-v1');
  await page.getByRole('button', { name: 'Assign owner' }).click();
  await expect(page.getByRole('dialog', { name: 'Assign NX-2026-000601' })).toBeVisible();
  await page.getByRole('button', { name: 'Assign owner' }).click();
  await expect.poll(() => assignment).not.toBeNull();
  expect(assignment).toMatchObject({ ownerUserId: 101, expectedVersion: 4 });
  expect(idempotencyKeys).toHaveLength(2);
  expect(idempotencyKeys[1]).toBe(idempotencyKeys[0]);
});

test('routing override and account reassignment require explicit reasons', async ({ page }) => {
  await authorizeManager(page);
  await mockOwnerOptions(page);
  await page.route('**/api/commercial-intelligence/routing-queue*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
    sourceId: 502, leadId: 602, nexoraSerial: 'NX-2026-000602', customerName: 'Beacon Systems',
    receivedAt: measuredAt, dueAt: measuredAt, reason: 'Confirm owner', recommendedOwnerUserId: 101,
    recommendedOwnerName: 'Avery Recommended', recommendationReason: 'LOWEST_VERIFIED_WORKLOAD',
    matchConfidence: 0.8, policyVersion: 'routing-v1', recommendationMeasuredAt: measuredAt,
    recommendedOwnerAvailable: true, recommendedOwnerCapacityPercent: 80,
    recommendedOwnerWorkloadPoints: 37, priority: 80, status: 'Open', overdue: false, version: 1,
  }]) }));
  await page.route('**/api/commercial-intelligence/account-ownership*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
    customerId: 701, customerName: 'Beacon Systems', ownerUserId: 101, ownerName: 'Avery Recommended',
    openLeads: 2, openQuotes: 1, pipelineGroups: [], lastActivityAt: measuredAt, version: 3,
  }]) }));
  let accountRequest: Record<string, unknown> | null = null;
  await page.route('**/api/commercial-intelligence/account-ownership/701/assign', async route => {
    accountRequest = route.request().postDataJSON();
    await route.fulfill({ status: 409, contentType: 'application/json', body: '{"error":"Ownership version changed"}' });
  });

  await page.goto('/sales/routing');
  await page.getByRole('button', { name: 'Assign', exact: true }).click();
  await page.getByRole('combobox', { name: 'Owner' }).click();
  await page.getByRole('option', { name: /Blair Alternate/ }).click();
  await expect(page.getByRole('button', { name: 'Confirm override' })).toBeDisabled();
  await page.getByLabel('Override reason').fill('Territory coverage for this account');
  await expect(page.getByRole('button', { name: 'Confirm override' })).toBeEnabled();

  await page.goto('/sales/accounts');
  await page.getByRole('button', { name: 'Reassign' }).click();
  await page.getByRole('combobox', { name: 'Owner' }).click();
  await page.getByRole('option', { name: /Blair Alternate/ }).click();
  await expect(page.getByRole('button', { name: 'Confirm owner' })).toBeDisabled();
  await page.getByLabel('Reassignment reason').fill('Balanced capacity and territory continuity');
  await page.getByRole('button', { name: 'Confirm owner' }).click();
  await expect.poll(() => accountRequest).not.toBeNull();
  expect(accountRequest).toMatchObject({ ownerUserId: 102, expectedVersion: 3, reason: 'Balanced capacity and territory continuity' });
  await expect(page.getByRole('dialog', { name: 'Reassign account owner' })).toHaveCount(0);
  await expect(page.getByText('Ownership changed. Refresh the row before trying again.')).toBeVisible();
});

test('performance shows denominator evidence, validates dates, and opens rep records', async ({ page }) => {
  await authorizeManager(page);
  await page.route('**/api/commercial-intelligence/performance?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
    generatedAt: measuredAt, from: '2026-07-01', to: '2026-07-31', scope: 'tenant', minimumConversionSample: 5,
    metrics: [{ key: 'won', label: 'Won', value: 2, unit: 'count' }, { key: 'lost', label: 'Lost', value: 1, unit: 'count' }, { key: 'decided', label: 'Decided outcomes', value: 3, unit: 'count' }],
    representatives: [{ userId: 101, name: 'Avery Recommended', email: 'avery@nexora.invalid', roleName: 'Sales Representative', activeLeads: 3, overdueLeads: 1, openRfqs: 2, draftQuotes: 1, followUpsDue: 2, pipelineGroups: [], wonQuotes: 2, lostQuotes: 1, decidedQuotes: 3, conversionEligible: false, conversionRate: null, activityCount: 12, opportunities: 4, quoteSent: 3, customerResponses: 2, averageResponseHours: 7.5, followUpsCreated: 4, completedFollowUps: 3, followUpsCompletedOnTime: 2, openFollowUps: 1, overdueFollowUps: 1, revenueByCurrency: [] }],
  }) }));
  await page.route('**/api/commercial-intelligence/reps/101?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ userId: 101, name: 'Avery Recommended', email: 'avery@nexora.invalid', roleName: 'Sales Representative', activeLeads: 3, overdueLeads: 1, openRfqs: 2, draftQuotes: 1, followUpsDue: 2, pipelineGroups: [], accountCount: 2, wonValueGroups: [], decidedQuotes: 3, conversionEligible: false, conversionRate: null, performanceFrom: '2026-07-01', performanceTo: '2026-07-31', recentActivity: [] }) }));

  await page.goto('/sales/performance');
  await expect(page.getByText('Insufficient data (3/5)')).toBeVisible();
  await expect(page.getByText('2/3 responses/sent')).toBeVisible();
  await page.getByRole('button', { name: 'Open records' }).click();
  await expect(page).toHaveURL(/\/sales\/reps\/101\?from=/);
  await expect(page.getByRole('heading', { name: 'Avery Recommended' })).toBeVisible();
  await page.goto('/sales/performance');
  await page.getByLabel('From').fill('2026-08-01');
  await page.getByRole('textbox', { name: 'To', exact: true }).fill('2026-07-01');
  await expect(page.getByRole('alert')).toContainText('From date must be earlier');
  await expect(page.getByRole('button', { name: 'Retry' })).toHaveCount(0);
});

test('dashboard-only managers do not receive an unauthorized rep-profile action', async ({ page }) => {
  await authorizeManager(page, ['Dashboard']);
  await page.route('**/api/commercial-intelligence/performance?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
    generatedAt: measuredAt, from: '2026-07-01', to: '2026-07-31', scope: 'tenant', minimumConversionSample: 5,
    metrics: [], representatives: [{ userId: 101, name: 'Avery Recommended', activeLeads: 0, overdueLeads: 0, openRfqs: 0, draftQuotes: 0, followUpsDue: 0, pipelineGroups: [], wonQuotes: 0, lostQuotes: 0, decidedQuotes: 0, conversionEligible: false, activityCount: 0, opportunities: 0, quoteSent: 0, customerResponses: 0, followUpsCreated: 0, completedFollowUps: 0, followUpsCompletedOnTime: 0, openFollowUps: 0, overdueFollowUps: 0, revenueByCurrency: [] }],
  }) }));

  await page.goto('/sales/performance');
  await expect(page.getByText('Users permission required')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Open records' })).toHaveCount(0);

  await page.route('**/api/dashboard/workload', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
    staleQuoteDays: 7, generatedAt: measuredAt, rows: [
      { userId: null, name: 'Unassigned', email: null, openLeads: 2, overdueLeads: 1, sentQuotes: 0, staleQuotes: 0, isUnassignedBucket: true },
      { userId: 101, name: 'Avery Recommended', email: 'avery@nexora.invalid', openLeads: 3, overdueLeads: 1, sentQuotes: 2, staleQuotes: 1, isUnassignedBucket: false },
    ],
  }) }));
  await page.goto('/dashboard/team');
  await expect(page.getByRole('heading', { name: 'Team workload' })).toBeVisible();
  await expect(page.getByText('Avery Recommended')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Avery Recommended' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Unassigned' })).toHaveCount(0);
});

test('representative directory uses the registered profile route without horizontal overflow', async ({ page }) => {
  await authorizeManager(page);
  await page.route('**/api/commercial-intelligence/reps', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
    userId: 101, name: 'Avery Recommended', email: 'avery@nexora.invalid', roleName: 'Sales Representative', activeLeads: 3, overdueLeads: 1, openRfqs: 2, draftQuotes: 1, followUpsDue: 2, pipelineGroups: [],
  }]) }));
  await page.route('**/api/commercial-intelligence/reps/101*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ userId: 101, name: 'Avery Recommended', email: 'avery@nexora.invalid', roleName: 'Sales Representative', activeLeads: 3, overdueLeads: 1, openRfqs: 2, draftQuotes: 1, followUpsDue: 2, pipelineGroups: [], accountCount: 2, wonValueGroups: [], decidedQuotes: 0, conversionEligible: false, conversionRate: null, performanceFrom: '2026-07-01', performanceTo: '2026-07-31', recentActivity: [{ id: 901, recordType: 'Quote', recordId: 801, nexoraSerial: 'NX-2026-000801', reference: 'Q-801', customerName: 'Atlas Controls', reason: 'Won', dueAt: measuredAt, priority: 'Recorded', actionRoute: '/sales/quotes/view/801', requiredModule: 'Quotations' }] }) }));
  await page.goto('/sales/reps');
  await page.getByRole('button', { name: 'Open', exact: true }).click();
  await expect(page).toHaveURL(/\/sales\/reps\/101$/);
  await expect(page.getByRole('region', { name: 'Representative activity' })).toContainText('NX-2026-000801');
  await page.getByRole('region', { name: 'Representative activity' }).getByRole('button', { name: 'Open' }).click();
  await expect(page).toHaveURL(/\/sales\/quotes\/view\/801$/);
  const dimensions = await page.evaluate(() => ({ width: document.documentElement.scrollWidth, viewport: window.innerWidth }));
  expect(dimensions.width).toBeLessThanOrEqual(dimensions.viewport);
});

/**
 * A plain sales rep: Leads edit, but not a manager. This is the lane that had no UI at all —
 * claim and release are the only routing verbs this role can reach, and nothing called them.
 */
async function authorizeRep(page: Page, userId = 102) {
  await page.addInitScript((id) => {
    const current = JSON.parse(localStorage.getItem('userData') ?? '{}');
    localStorage.setItem('userData', JSON.stringify({
      ...current, id, isManager: false, isSuperAdmin: false,
      permissions: [{ id: 9400, moduleId: 9400, moduleName: 'Leads', roleId: current.roleId ?? 1, canCreate: true, canEdit: true, canDelete: false }],
    }));
  }, userId);
}

const queueRow = {
  sourceId: 501,
  leadId: 601,
  nexoraSerial: 'NX-2026-000601',
  customerName: 'Atlas Controls',
  receivedAt: '2026-07-30T16:00:00Z',
  dueAt: '2026-07-30T17:00:00Z',
  reason: 'Confirm a sales owner',
  recommendedOwnerUserId: null,
  recommendedOwnerName: null,
  recommendationReason: 'NO_MATCH_EVIDENCE',
  matchConfidence: 0,
  policyVersion: 'routing-v1',
  recommendationMeasuredAt: measuredAt,
  recommendedOwnerAvailable: null,
  recommendedOwnerCapacityPercent: null,
  recommendedOwnerWorkloadPoints: null,
  claimedByUserId: null,
  claimedByName: null,
  claimedUntil: null,
  claimExpired: false,
  priority: 90,
  status: 'Open',
  overdue: true,
  version: 4,
};

test('a sales rep can pull work from the shared pool and is told a claim is not ownership', async ({ page }) => {
  await authorizeRep(page);
  await page.route('**/api/commercial-intelligence/routing-queue*', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify([queueRow]),
  }));
  let claim: Record<string, unknown> | null = null;
  await page.route('**/api/commercial-routing/queue/501/claim', async route => {
    claim = route.request().postDataJSON();
    await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
  });

  await page.goto('/sales/routing');
  await expect(page.getByRole('region', { name: 'Lead routing queue' })).toContainText('Unclaimed');
  // A rep may claim but may not assign: assignment is manager-gated on the server, so offering
  // the button would only produce a 403.
  await expect(page.getByRole('button', { name: 'Assign', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Claim' }).click();

  await expect.poll(() => claim).not.toBeNull();
  // The row version travels with the claim so two reps racing produce a 409 for the loser
  // rather than a silent steal.
  expect(claim).toMatchObject({ expectedVersion: 4 });
});

test('an expired lease is shown as free rather than as still held', async ({ page }) => {
  await authorizeRep(page);
  await page.route('**/api/commercial-intelligence/routing-queue*', route => route.fulfill({
    status: 200, contentType: 'application/json',
    // Status still reads Claimed: nothing flips it back when the lease runs out. The row must be
    // rendered from the lease, not from the status.
    body: JSON.stringify([{ ...queueRow, status: 'Claimed', claimedByUserId: 101, claimedByName: 'Avery Recommended', claimedUntil: '2026-07-30T16:30:00Z', claimExpired: true }]),
  }));

  await page.goto('/sales/routing');
  const queue = page.getByRole('region', { name: 'Lead routing queue' });
  await expect(queue).toContainText('Claimed by Avery Recommended');
  await expect(queue).toContainText('anyone may claim it');
  await expect(page.getByRole('button', { name: 'Claim' })).toBeEnabled();
});

test('a manager can give a representative the governed profile routing requires', async ({ page }) => {
  await authorizeManager(page);
  await page.route('**/api/commercial-intelligence/reps', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
    userId: 103, name: 'Casey Unprofiled', email: 'casey@nexora.invalid', roleName: 'Sales Representative', activeLeads: 0, overdueLeads: 0, openRfqs: 0, draftQuotes: 0, followUpsDue: 0, pipelineGroups: [],
  }]) }));
  await page.route('**/api/commercial-intelligence/reps/routing-profiles', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
    userId: 103, name: 'Casey Unprofiled', email: 'casey@nexora.invalid', roleName: 'Sales Representative',
    hasProfile: false, profileEffectiveNow: false, isRoutingEligible: null, capacityPercent: null,
    distributionWeight: null, territoryKeys: [], productCategoryKeys: [], effectiveFromUtc: null,
    effectiveToUtc: null, version: 0, updatedAtUtc: null, updatedBy: null, isAvailable: false,
    eligibilityReason: 'Governed Sales Rep profile is required', measuredCapacityPercent: null,
    workloadPoints: null, policyVersion: null, measuredAtUtc: null,
  }]) }));
  let saved: Record<string, unknown> | null = null;
  await page.route('**/api/commercial-intelligence/reps/103/routing-profile', async route => {
    saved = route.request().postDataJSON();
    await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
  });

  await page.goto('/sales/reps');
  // The empty-table state is the one that matters: with no profile rows the engine can assign
  // nobody at all, and before this screen there was no way to see or fix that.
  await expect(page.getByText('No representative in this business unit is currently eligible')).toBeVisible();
  await expect(page.getByRole('region', { name: 'Sales representatives' })).toContainText('No profile');
  await page.getByRole('button', { name: 'Enable routing' }).click();
  await page.getByRole('button', { name: 'Save profile' }).click();

  await expect.poll(() => saved).not.toBeNull();
  // 0 is the create sentinel; sending anything else would be refused as a version conflict.
  expect(saved).toMatchObject({ isRoutingEligible: true, capacityPercent: 100, expectedVersion: 0 });
});
