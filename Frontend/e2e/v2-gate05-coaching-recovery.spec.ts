import { expect, test, type Page } from '@playwright/test';

const generatedAt = '2026-07-29T14:00:00Z';
const evidence = [{ recordType: 'CustomerQuote', recordId: 301, reference: 'Q-RELEASE-01C', occurredOn: generatedAt, role: 'Outcome evidence' }];

const finding = {
  findingKey: 'a'.repeat(64),
  code: 'SLOW_FIRST_RESPONSE',
  salesRepUserId: 1,
  salesRepName: 'Release Manager',
  customerId: 401,
  customerName: 'Release 01C Test Customer',
  aggregateType: 'SalesRep',
  aggregateId: 1,
  sourceVersion: 'sha256:001122',
  reference: 'REP-1',
  nexoraSerial: 'NXR-2026-000101',
  severity: 'HIGH',
  observedValue: 31,
  observedUnit: 'hours',
  thresholdValue: 24,
  sampleSize: 8,
  confidence: 0.92,
  asOf: generatedAt,
  recommendation: 'Review delayed first responses with the sales representative',
  actionRoute: '/sales/reps/1',
  policyVersion: 'v2.5.0',
  evidence,
  latestAcknowledgement: null,
};

const recovery = {
  recoveryKey: 'quote-follow-up:301:sha-002',
  code: 'QUOTE_NOT_FOLLOWED',
  sourceType: 'CustomerQuote',
  sourceId: 301,
  sourceVersion: 'sha256:002233',
  customerId: 401,
  customerName: 'Release 01C Test Customer',
  ownerUserId: 1,
  ownerName: 'Release Manager',
  nexoraSerial: 'NXR-2026-000101',
  severity: 'CRITICAL',
  title: 'Customer Quote needs follow-up',
  explanation: 'The Quote is open and no completed follow-up is recorded in the policy window.',
  recommendedAction: 'Contact the customer and record the outcome.',
  actionRoute: '/sales/quotes/view/301',
  dueAt: '2026-07-28T14:00:00Z',
  sampleSize: 1,
  confidence: 1,
  evidence,
};

const coachingResponse = () => ({
  generatedAt,
  policyVersion: 'v2.5.0',
  scope: 'tenant',
  dataCompleteness: { status: 'complete', incompleteSources: [] },
  coachingFindings: [{ ...finding }],
  recoveryOpportunities: [{ ...recovery }],
});

async function setRole(page: Page, manager: boolean) {
  await page.addInitScript(({ isManager }) => {
    const current = JSON.parse(localStorage.getItem('userData') ?? '{}');
    const permissions = Array.isArray(current.permissions) ? current.permissions : [];
    for (const moduleName of ['Customers', 'Sales Coaching']) {
      if (!permissions.some((item: { moduleName?: string }) => item.moduleName === moduleName)) {
        permissions.push({ id: 9000 + permissions.length, roleId: current.roleId ?? 1, moduleId: 9000 + permissions.length, moduleName, canCreate: false, canEdit: isManager, canDelete: false });
      }
    }
    localStorage.setItem('userData', JSON.stringify({ ...current, isManager, permissions }));
  }, { isManager: manager });
}

async function mockSalesToday(page: Page, response = coachingResponse()) {
  await page.route('**/api/commercial-intelligence/sales-today', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ generatedAt, scope: 'tenant', metrics: [], attentionItems: [] }),
  }));
  await page.route('**/api/opportunity-priorities?*', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ items: [], total: 0, pageNumber: 1, pageSize: 10, accessScope: 'tenant', mode: 'Shadow', generatedAtUtc: generatedAt, cohort: { eligibleRecommendations: 0, insufficientEvidenceRecommendations: 0, recommendationsWithObservedOutcome: 0, accuracyStatus: 'insufficient_evidence' } }),
  }));
  await page.route('**/api/commercial-intelligence/coaching-recovery?*', route => {
    const url = new URL(route.request().url());
    expect(url.searchParams.get('from')).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(url.searchParams.get('to')).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(response) });
  });
}

test('manager reviews evidence, uses keyboard tabs, drills down, and records a cohort-bound acknowledgement', async ({ page }) => {
  await setRole(page, true);
  const response = coachingResponse();
  await mockSalesToday(page, response);

  let submitted: Record<string, unknown> | undefined;
  let idempotencyKey: string | undefined;
  await page.route('**/api/commercial-intelligence/coaching/*/acknowledgements', async route => {
    submitted = route.request().postDataJSON() as Record<string, unknown>;
    idempotencyKey = route.request().headers()['idempotency-key'];
    response.coachingFindings[0].latestAcknowledgement = {
      disposition: String(submitted.disposition),
      reason: String(submitted.reason),
      acknowledgedByName: 'Release Manager',
      acknowledgedAt: generatedAt,
    };
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(response.coachingFindings[0].latestAcknowledgement) });
  });

  await page.goto('/sales/today');
  await expect(page.getByRole('heading', { name: 'Coaching and recovery' })).toBeVisible();
  await expect(page.getByText(finding.recommendation)).toBeVisible();
  await page.getByRole('button', { name: 'Evidence (1)' }).click();
  await expect(page.getByText(/Outcome evidence: Q-RELEASE-01C/)).toBeVisible();

  const findingsTab = page.getByRole('tab', { name: /Coaching findings/ });
  await findingsTab.focus();
  await findingsTab.press('ArrowRight');
  await expect(page.getByRole('tab', { name: /Recovery opportunities/ })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText(recovery.title)).toBeVisible();
  await page.getByRole('tab', { name: /Coaching findings/ }).click();

  await page.getByRole('button', { name: 'Acknowledge finding' }).click();
  const dialog = page.getByRole('dialog', { name: 'Acknowledge coaching finding' });
  await expect(dialog.getByLabel('Decision reason')).toBeFocused();
  await dialog.getByLabel('Decision reason').fill('Manager reviewed the source evidence and scheduled a coaching discussion.');
  await dialog.getByRole('button', { name: 'Record acknowledgement' }).click();
  await expect(dialog).toHaveCount(0);
  expect(idempotencyKey).toMatch(/^[0-9a-f-]{36}$/i);
  expect(submitted).toMatchObject({ disposition: 'ACKNOWLEDGED', reason: 'Manager reviewed the source evidence and scheduled a coaching discussion.' });
  expect(submitted?.from).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  expect(submitted?.to).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  await expect(page.getByText(/Acknowledged: Manager reviewed the source evidence/)).toBeVisible();

  await page.getByRole('button', { name: 'Open source' }).first().click();
  await expect(page).toHaveURL(/\/sales\/reps\/1$/);
});

test('non-manager sees governed findings without acknowledgement actions', async ({ page }) => {
  await setRole(page, false);
  await mockSalesToday(page);
  await page.goto('/sales/today');
  await expect(page.getByText(finding.recommendation)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Acknowledge finding' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Open source' }).first()).toBeVisible();
});

test('coaching recovery exposes error retry and explicit empty states', async ({ page }) => {
  await setRole(page, true);
  await page.route('**/api/commercial-intelligence/sales-today', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ generatedAt, scope: 'tenant', metrics: [], attentionItems: [] }) }));
  await page.route('**/api/opportunity-priorities?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], total: 0, pageNumber: 1, pageSize: 10, accessScope: 'tenant', mode: 'Shadow', generatedAtUtc: generatedAt, cohort: { eligibleRecommendations: 0, insufficientEvidenceRecommendations: 0, recommendationsWithObservedOutcome: 0, accuracyStatus: 'insufficient_evidence' } }) }));
  let attempts = 0;
  await page.route('**/api/commercial-intelligence/coaching-recovery?*', route => {
    attempts += 1;
    if (attempts <= 2) return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'temporary' }) });
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ...coachingResponse(), coachingFindings: [], recoveryOpportunities: [] }) });
  });

  await page.goto('/sales/today');
  await expect(page.getByText('This persisted view could not be loaded. No empty result has been assumed.')).toBeVisible();
  await page.getByRole('button', { name: 'Retry' }).last().click();
  await expect(page.getByText('No coaching findings require attention for this cohort.')).toBeVisible();
  await page.getByRole('tab', { name: /Recovery opportunities/ }).click();
  await expect(page.getByText('No recovery opportunities require attention for this cohort.')).toBeVisible();
});

async function mockCustomer360(page: Page) {
  const health = {
    customerId: 401,
    generatedAt,
    dataCompleteness: { status: 'complete', incompleteSources: [] },
    period: { from: '2026-05-01', to: '2026-07-29', previousFrom: '2026-02-01', previousTo: '2026-04-30' },
    rfqTrend: { status: 'DECLINING', currentCount: 4, previousCount: 7, changePercent: -42.9 },
    quoteCoverage: { status: 'MEASURED', rfqCount: 4, quotedRfqCount: 3, coveragePercent: 75 },
    quoteDecisions: { status: 'MEASURED', decidedCount: 2, wonCount: 1, lostCount: 1, pendingCount: 1 },
    conversion: { status: 'MEASURED', ratePercent: 50, sampleSize: 2 },
    acceptedPrices: [{ productId: 1, partNumber: 'NXR-TEST-PART-001', description: 'Fixture part', currencyCode: 'USD', quantity: 4, unitPrice: 25, acceptedOn: generatedAt, evidence: evidence[0] }],
    margin: { status: 'INSUFFICIENT_EVIDENCE', grossMarginPercent: null, reason: 'No governed landed-cost evidence.' },
    revisionBurden: { status: 'MEASURED', revisionCount: 2, inquiryCount: 4, changedFieldCount: 1, comparedFieldCount: 4, fieldChangePercent: 25, changedLineCount: 2, comparedLineCount: 4, lineChangePercent: 50 },
    followUp: { status: 'ATTENTION_REQUIRED', openCount: 2, overdueCount: 1, effectivenessPercent: 40 },
    lastCommercialActivity: evidence[0],
    healthBand: 'AT RISK',
    healthReasons: ['RFQ volume declined in the measured period.', 'One follow-up is overdue.'],
    opportunities: [{ code: 'RECOVER_QUOTE', title: 'Recover open Quote', explanation: 'A decided outcome is still missing.', actionRoute: '/dashboard', evidence }],
    nextBestAction: { title: 'Complete the overdue follow-up', explanation: 'Q-RELEASE-01C is overdue and remains open.', actionRoute: '/dashboard', evidence },
  };
  await page.route('**/api/Customer/401', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: 401, docId: 'CUST-401', name: 'Release 01C Test Customer', contactEmail: 'buyer@release01c.test', isActive: true }) }));
  await page.route('**/api/Contact?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }));
  await page.route('**/api/intelligence/customers/401/context', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ customerId: 401, customerName: 'Release 01C Test Customer', totalQuotes: 1, wonQuotes: 1, lostQuotes: 0, winRatePct: 100, ordersLast24Months: 0, orderValueLast24Months: 0, avgQuoteTotal: 100, avgMarginPct: null, lastQuoteDate: generatedAt, recentQuotes: [], recentItemPrices: [], recentRfqs: [], recentOrders: [], demandProfile: [], generatedAt }) }));
  await page.route('**/api/commercial-learning/customers/401', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ customerId: 401, customerName: 'Release 01C Test Customer', inquiryCount: 4, quoteCount: 3, decidedCount: 2, wonCount: 1, lostCount: 1, pendingCount: 1, conversionRatePercent: 50, wonValues: [], lossReasons: [], evidence: [] }) }));
  await page.route('**/api/commercial-intelligence/account-ownership?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{ customerId: 401, customerName: 'Release 01C Test Customer', ownerUserId: 1, ownerName: 'Release Manager', openLeads: 1, openQuotes: 1, pipelineGroups: [], lastActivityAt: generatedAt, version: 1 }]) }));
  await page.route('**/api/intelligence/customers/401/health?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(health) }));
}

test('Customer 360 renders server-authoritative health and remains within desktop and mobile viewports', async ({ page }) => {
  await setRole(page, true);
  await mockCustomer360(page);
  await page.goto('/customers/401');
  await expect(page.getByRole('heading', { name: 'Release 01C Test Customer' })).toBeVisible();
  await expect(page.getByText('AT RISK', { exact: true })).toBeVisible();
  await expect(page.getByText('Complete the overdue follow-up', { exact: true })).toBeVisible();
  await expect(page.getByText(/INSUFFICIENT_EVIDENCE: No governed landed-cost evidence/)).toBeVisible();
  await expect(page.getByRole('table', { name: 'Accepted customer prices' })).toContainText('USD 25');
  const dimensions = await page.evaluate(() => ({ documentWidth: document.documentElement.scrollWidth, viewportWidth: window.innerWidth }));
  expect(dimensions.documentWidth).toBeLessThanOrEqual(dimensions.viewportWidth);
});
