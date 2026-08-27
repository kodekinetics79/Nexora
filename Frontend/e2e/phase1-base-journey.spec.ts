import { expect, test, type Page } from '@playwright/test';
import { loginThroughUi } from './support/login';
import { requireEnv } from './support/environment';

/**
 * Truthful pilot gate for the governed Lead decision boundary:
 *
 *   immutable current Lead revision + retained evidence
 *   → human fit assessment
 *   → complete partial-bid participation decision
 *   → RFQ Promotion copies only Bid lines
 *
 * The disposable PostgreSQL seed creates starting conditions only. This browser test creates the
 * assessment, participation decision, promotion receipt, and RFQ through the real HTTP API. The
 * retired conversion route is intentionally absent from this gate.
 */

const env = () => requireEnv('Governed Lead pilot gate',
  'E2E_API_URL', 'E2E_GOLDEN_SALES_EMAIL', 'E2E_GOLDEN_OUTSIDER_EMAIL',
  'E2E_GOLDEN_PASSWORD', 'E2E_GOLDEN_LEAD_ID', 'E2E_GOLDEN_TENANT_A',
  'E2E_GOLDEN_TENANT_B');

type WorkbenchLine = {
  revisionLineId: number;
  manufacturerPartNumber?: string | null;
  quantity?: number | null;
  unitOfMeasure?: string | null;
  currency?: string | null;
  bestMatchProductId?: number | null;
  needsAttention?: boolean;
  verificationStatus: string;
};

type Workbench = {
  leadRevisionId: number;
  decisionVersion: number;
  participationVersion?: number | null;
  participationStatus: string;
  verificationStatus: string;
  sourceCoverage?: { coveredLines: number; totalLines: number } | null;
  evidence: Array<{ sourceAvailable: boolean }>;
  lines: WorkbenchLine[];
  reasonCodes: Array<{ code: string; appliesTo: string[] }>;
};

async function token(page: Page): Promise<string> {
  const value = await page.evaluate(() => localStorage.getItem('token'));
  if (!value) throw new Error('Authenticated session carries no access token.');
  return value;
}

async function api(
  page: Page,
  bearer: string | null,
  method: string,
  path: string,
  body?: unknown,
  idempotencyKey?: string,
) {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (bearer) headers.Authorization = `Bearer ${bearer}`;
  if (idempotencyKey) headers['Idempotency-Key'] = idempotencyKey;
  return page.request.fetch(`${env().E2E_API_URL}${path}`, {
    method,
    headers,
    data: body === undefined ? undefined : JSON.stringify(body),
  });
}

async function rfqCountForLead(page: Page, bearer: string, leadId: number): Promise<number> {
  const response = await api(page, bearer, 'get', '/api/Rfq?pageNumber=1&pageSize=250');
  expect(response.ok(), await response.text()).toBeTruthy();
  const payload = await response.json();
  return (payload.items ?? []).filter((row: { leadId?: number }) => row.leadId === leadId).length;
}

test('pilot gate — governed Lead decision promotes approved lines to exactly one RFQ', async ({ page }) => {
  const values = env();
  const leadId = Number(values.E2E_GOLDEN_LEAD_ID);
  await loginThroughUi(page, {
    email: values.E2E_GOLDEN_SALES_EMAIL,
    password: values.E2E_GOLDEN_PASSWORD,
    businessUnitId: values.E2E_GOLDEN_TENANT_A,
  });
  const bearer = await token(page);
  expect(await rfqCountForLead(page, bearer, leadId)).toBe(0);

  const initialResponse = await api(page, bearer, 'get', `/api/leads/${leadId}/decision-workbench`);
  expect(initialResponse.ok(), await initialResponse.text()).toBeTruthy();
  const initial = await initialResponse.json() as Workbench;
  expect(initial.lines).toHaveLength(6);
  expect(initial.participationStatus).toBe('NONE');
  expect(initial.participationVersion).toBeNull();
  expect(initial.verificationStatus).toBe('VERIFIED');
  expect(initial.sourceCoverage).toEqual({ coveredLines: 6, totalLines: 6 });
  expect(initial.evidence.length).toBeGreaterThan(0);
  expect(initial.evidence.every((item) => item.sourceAvailable)).toBeTruthy();
  expect(initial.lines.every((line) => line.verificationStatus === 'VERIFIED')).toBeTruthy();

  await page.goto(`/procurement/leads/${leadId}/workbench`);
  await expect(page.getByRole('heading', { name: 'Source evidence' })).toBeVisible();
  await page.getByRole('tab', { name: '3. Fit & Participation' }).click();
  await expect(page.getByRole('heading', { name: 'Fit assessment' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Participation by line' })).toBeVisible();
  await page.getByRole('tab', { name: '4. Promote' }).click();
  await expect(page.getByRole('heading', { name: 'RFQ promotion' })).toBeVisible();

  const prematurePromotion = await api(
    page, bearer, 'post', `/api/leads/${leadId}/promote-to-rfq`, {
      expectedLeadRevisionId: initial.leadRevisionId,
      expectedDecisionVersion: initial.decisionVersion,
      expectedParticipationVersion: 0,
    }, `pilot-premature-promotion:${leadId}`);
  expect(prematurePromotion.status(), 'promotion without committed participation must fail').toBe(409);
  expect(await rfqCountForLead(page, bearer, leadId)).toBe(0);

  const fitRequest = {
    expectedLeadRevisionId: initial.leadRevisionId,
    expectedDecisionVersion: initial.decisionVersion,
    expectedFitVersion: null,
    overallDecision: 'FIT',
    rationale: 'The reviewer confirmed eligibility, capability, delivery, compliance, and commercials.',
    criteria: ['ELIGIBILITY', 'CAPABILITY', 'DELIVERY', 'COMPLIANCE', 'COMMERCIAL']
      .map((code) => ({ code, decision: 'PASS', note: 'Confirmed by the pilot reviewer.' })),
  };
  const fitKey = `pilot-fit:${leadId}:${initial.leadRevisionId}`;
  const fit = await api(page, bearer, 'put', `/api/leads/${leadId}/fit-assessment`, fitRequest, fitKey);
  expect(fit.ok(), await fit.text()).toBeTruthy();
  const fitVersion = (await fit.json()).version as number;
  expect(fitVersion).toBe(1);
  const fitReplay = await api(page, bearer, 'put', `/api/leads/${leadId}/fit-assessment`, fitRequest, fitKey);
  expect(fitReplay.ok(), await fitReplay.text()).toBeTruthy();
  expect((await fitReplay.json()).version).toBe(fitVersion);

  const noBidReason = initial.reasonCodes.find((reason) => reason.appliesTo.includes('NoBid'))?.code;
  expect(noBidReason, 'fixture must expose a governed no-bid reason').toBeTruthy();
  const decisions = initial.lines.map((line) => {
    if (line.manufacturerPartNumber === 'GOLD-NOQT-0005') {
      return {
        revisionLineId: line.revisionLineId,
        decision: 'NoBid',
        reasonCode: noBidReason,
        note: 'This obsolete part is outside the approved product scope.',
      };
    }
    return {
      revisionLineId: line.revisionLineId,
      decision: 'Bid',
      productId: line.bestMatchProductId ?? undefined,
      quantity: line.quantity && line.quantity > 0 ? line.quantity : 25,
      unitOfMeasure: line.unitOfMeasure || 'EA',
      currency: line.currency || 'SAR',
      note: line.needsAttention
        ? 'The bid desk reviewed the source and confirmed this corrected commercial value.'
        : undefined,
    };
  });
  expect(decisions.filter((line) => line.decision === 'Bid')).toHaveLength(5);
  expect(decisions.filter((line) => line.decision === 'NoBid')).toHaveLength(1);

  const participationRequest = {
    expectedLeadRevisionId: initial.leadRevisionId,
    expectedDecisionVersion: initial.decisionVersion,
    expectedParticipationVersion: null,
    commit: true,
    notes: 'Pilot gate partial-bid decision.',
    lines: decisions,
  };
  const participationKey = `pilot-participation:${leadId}:${initial.leadRevisionId}`;
  const participation = await api(
    page, bearer, 'put', `/api/leads/${leadId}/participation`, participationRequest, participationKey);
  expect(participation.ok(), await participation.text()).toBeTruthy();
  const participationResult = await participation.json();
  expect(participationResult.participationStatus).toBe('COMMITTED');
  expect(participationResult.participationVersion).toBe(1);
  const participationReplay = await api(
    page, bearer, 'put', `/api/leads/${leadId}/participation`, participationRequest, participationKey);
  expect(participationReplay.ok(), await participationReplay.text()).toBeTruthy();
  expect((await participationReplay.json()).participationVersion).toBe(1);

  const committedResponse = await api(page, bearer, 'get', `/api/leads/${leadId}/decision-workbench`);
  expect(committedResponse.ok(), await committedResponse.text()).toBeTruthy();
  const committed = await committedResponse.json() as Workbench;
  expect(committed.participationStatus).toBe('COMMITTED');
  expect(committed.participationVersion).toBe(1);

  const promotionRequest = {
    expectedLeadRevisionId: committed.leadRevisionId,
    expectedDecisionVersion: committed.decisionVersion,
    expectedParticipationVersion: committed.participationVersion,
  };
  const promotionKey = `pilot-promotion:${leadId}:${committed.leadRevisionId}`;
  const promotion = await api(
    page, bearer, 'post', `/api/leads/${leadId}/promote-to-rfq`, promotionRequest, promotionKey);
  expect(promotion.ok(), await promotion.text()).toBeTruthy();
  const receipt = await promotion.json();
  expect(receipt.rfqId).toBeGreaterThan(0);
  expect(receipt.promotedLineCount).toBe(5);

  const promotionReplay = await api(
    page, bearer, 'post', `/api/leads/${leadId}/promote-to-rfq`, promotionRequest, promotionKey);
  expect(promotionReplay.ok(), await promotionReplay.text()).toBeTruthy();
  expect((await promotionReplay.json()).rfqId).toBe(receipt.rfqId);
  expect(await rfqCountForLead(page, bearer, leadId)).toBe(1);

  const rfqResponse = await api(
    page, bearer, 'get', `/api/Rfq/${receipt.rfqId}?businessUnitId=${values.E2E_GOLDEN_TENANT_A}`);
  expect(rfqResponse.ok(), await rfqResponse.text()).toBeTruthy();
  const rfq = await rfqResponse.json();
  expect(rfq.leadId).toBe(leadId);
  expect(rfq.rfqitems).toHaveLength(5);

  await page.goto(`/procurement/rfqs/view/${receipt.rfqId}`);
  await expect(page).toHaveURL(new RegExp(`/procurement/rfqs/view/${receipt.rfqId}$`));
  await expect(page.getByText(receipt.rfqNumber, { exact: false }).first()).toBeVisible();
});

test('pilot gate — anonymous and cross-tenant workbench access is non-disclosing', async ({ page }) => {
  const values = env();
  const leadId = Number(values.E2E_GOLDEN_LEAD_ID);
  const anonymous = await api(page, null, 'get', `/api/leads/${leadId}/decision-workbench`);
  expect(anonymous.status()).toBe(401);

  await loginThroughUi(page, {
    email: values.E2E_GOLDEN_OUTSIDER_EMAIL,
    password: values.E2E_GOLDEN_PASSWORD,
    businessUnitId: values.E2E_GOLDEN_TENANT_B,
  });
  const outsider = await token(page);
  const foreignWorkbench = await api(page, outsider, 'get', `/api/leads/${leadId}/decision-workbench`);
  expect(foreignWorkbench.status()).toBe(404);
  const foreignPromotion = await api(
    page, outsider, 'post', `/api/leads/${leadId}/promote-to-rfq`,
    { expectedLeadRevisionId: 1, expectedDecisionVersion: 1, expectedParticipationVersion: 1 },
    `pilot-cross-tenant:${leadId}`);
  expect(foreignPromotion.status()).toBe(404);
});
