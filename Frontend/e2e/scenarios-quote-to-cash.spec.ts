/**
 * Scenario testing of CUSTOMER QUOTE → SEND → CLIENT PO → ORDER → SHIPMENT → DELIVERY/POD →
 * INVOICE → PAYMENT against a disposable real stack (docs/audit/SCENARIOS-QUOTE-TO-CASH-2026-09-05.md).
 *
 * Every test asserts PERSISTED state through the API; where a screen owns the verb the screen's
 * wording is asserted too. A refusal is not a defect; an unexplained, unreachable or one-way
 * refusal is. Known defects are asserted with expect.soft so a run records them as failures while
 * the scenario keeps walking on the documented workaround.
 *
 * Harness: scripts/e2e/run-enterprise-commercial-journey.sh with E2E_KEEP_STACK=1 and
 * Notifications__OutboundGuard__Mode=DraftOnly, so a quote "send" is safe — the row lands in
 * quote_delivery_requests, the guard withholds the message, and the dispatcher dead-letters the
 * delivery as UNCERTAIN. That is the harness, and it is also exactly the shape scenario 2b asks for.
 *
 * Serial and self-provisioning on a FRESH fixture; the ids below are stable within a run and every
 * command carries a run-scoped idempotency key.
 */
import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { api, jsonOk, loginAs, loginAsOtherTenant, required, requiredNumber } from './support/core-commercial';
import { loginThroughUi } from './support/login';

test.describe.configure({ mode: 'serial' });

const RUN = process.env.E2E_SCENARIO_RUN ?? new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 14);
const key = (name: string) => ({ 'Idempotency-Key': `scn-${RUN}-${name}`, 'X-Correlation-ID': `scn-${RUN}-${name}` });
const nowIso = () => new Date().toISOString();

type Json = Record<string, any>;
const sendQuoteId = () => requiredNumber('E2E_CORE_SEND_QUOTE_ID');
const mainQuoteId = () => requiredNumber('E2E_CORE_QUOTE_ID');
const sixLineRfqId = () => requiredNumber('E2E_CORE_QUOTE_DRAFT_RFQ_ID');
const mainRfqId = () => requiredNumber('E2E_CORE_RFQ_ID');
const tenantId = () => requiredNumber('E2E_MANAGER_BUSINESS_UNIT_ID');
const currencyId = () => requiredNumber('E2E_CORE_CURRENCY_ID');

/** Ids the serial chain hands from one scenario to the next. */
const state = {
  sixLineId: 0,          // the six-line quote on the quoteless RFQ (R1)
  sixLineRevisionId: 0,  // its revision (R2), the one the Client PO is filed against
  orderId: 0,
  orderItemAtp: 0,
  orderItemPartial: 0,
  shipmentShort: 0,      // ATP line, confirmed one short
  shipmentRace: 0,       // partial line, two units, used for the concurrency scenario
  proofLineShort: 0,
  acceptedAtp: 0,
  invoiceId: 0,
  approvalId: '',
};

async function status(response: APIResponse): Promise<{ status: number; body: Json | string }> {
  const text = await response.text();
  try { return { status: response.status(), body: JSON.parse(text) }; } catch { return { status: response.status(), body: text }; }
}

const sentence = (body: Json | string): string =>
  typeof body === 'string' ? body : String(body.detail ?? body.message ?? body.error ?? body.title ?? JSON.stringify(body));

const note = (type: string, description: string) => test.info().annotations.push({ type, description });

async function loginAsOwner(page: Page): Promise<string> {
  // Every acceptance user shares the fixture password; the Owner is the second manager the
  // below-floor approval needs (the requester may not approve their own hold).
  await loginThroughUi(page, {
    email: process.env.E2E_OWNER_EMAIL || 'owner@release01c1.local',
    password: required('E2E_MANAGER_PASSWORD'),
    businessUnitId: required('E2E_MANAGER_BUSINESS_UNIT_ID'),
  });
  const token = await page.evaluate(() => localStorage.getItem('token'));
  if (!token) throw new Error('Owner session did not contain an access token.');
  return token;
}

/** 'finance' is not a RoleName the shared helper knows; the fixture exports E2E_FINANCE_* like the others. */
async function loginAsFinance(page: Page): Promise<string> {
  await loginThroughUi(page, {
    email: required('E2E_FINANCE_EMAIL'), password: required('E2E_FINANCE_PASSWORD'),
    businessUnitId: required('E2E_FINANCE_BUSINESS_UNIT_ID'),
  });
  const token = await page.evaluate(() => localStorage.getItem('token'));
  if (!token) throw new Error('Finance session did not contain an access token.');
  return token;
}

async function quote(page: Page, token: string, id: number): Promise<Json> {
  return jsonOk<Json>(await api(page, token, 'get', `/api/Quote/${id}`));
}

async function readiness(page: Page, token: string, id: number): Promise<Json> {
  return jsonOk<Json>(await api(page, token, 'get', `/api/Quote/${id}/send-readiness`));
}

const blockerCodes = (ready: Json): string[] => (ready.blockers as Json[]).map((b) => b.code as string);

/** The harness's own blocker: a DraftOnly stack can never transmit. Everything else is product. */
const HARNESS_MAIL_BLOCKERS = ['OUTBOUND_MAIL_NOT_CONFIGURED', 'OUTBOUND_MAIL_DRAFT_ONLY'];
const productBlockers = (ready: Json) => blockerCodes(ready).filter((c) => !HARNESS_MAIL_BLOCKERS.includes(c));

async function attest(page: Page, token: string, id: number, reference = 'Morgan Manager'): Promise<APIResponse> {
  return api(page, token, 'post', `/api/Quote/${id}/price-attestation`, { source: 'SALES_MANAGER', sourceReference: reference });
}

async function send(page: Page, token: string, id: number, recipient = 'procurement@abc-engineering.local'): Promise<APIResponse> {
  return api(page, token, 'post', `/api/Quote/${id}/email?recipientEmail=${encodeURIComponent(recipient)}`, {});
}

async function deliveryState(page: Page, token: string, id: number) {
  const ready = await readiness(page, token, id);
  return { outcome: ready.deliveryOutcome as string | null, inFlight: Boolean(ready.deliveryInFlight), blockers: blockerCodes(ready) };
}

function quoteUpdateBody(current: Json, overrides: { currencyId?: number; priceFor?: (item: Json) => number }): Json {
  return {
    id: current.id, quoteNo: current.quoteNo, customerId: current.customerId, quoteDate: current.quoteDate,
    validUntil: current.validUntil, statusId: current.statusId, currencyId: overrides.currencyId ?? current.currencyId,
    headerRemarks: current.headerRemarks, modifiedBy: 'scenario',
    quoteItems: (current.quoteItems as Json[]).map((item) => {
      const unitPrice = overrides.priceFor ? overrides.priceFor(item) : item.unitPrice;
      return {
        id: item.id, rfqItemId: item.rfqItemId, productId: item.productId, itemDescription: item.itemDescription,
        unitOfMeasure: item.unitOfMeasure, quantity: item.quantity, unitPrice, totalAmount: item.quantity * unitPrice,
        taxCategory: item.taxCategory,
      };
    }),
  };
}

async function updateQuote(page: Page, token: string, id: number, overrides: { currencyId?: number; priceFor?: (item: Json) => number }) {
  const current = await quote(page, token, id);
  return status(await api(page, token, 'put', `/api/Quote/${id}`, quoteUpdateBody(current, overrides)));
}

async function markSentByHand(page: Page, token: string, id: number, tag: string): Promise<Json> {
  const current = await quote(page, token, id);
  await jsonOk(await api(page, token, 'post', `/api/Quote/${id}/status`, {
    targetStatusCode: 'SENT', expectedVersion: current.lifecycleVersion, reasonNotes: 'Sent by hand for the scenario run',
    idempotencyKey: `scn-${RUN}-${tag}`, correlationId: `scn-${RUN}-${tag}`,
  }));
  return quote(page, token, id);
}

function lineByPart(sixLine: Json, part: string): Json {
  const line = (sixLine.quoteItems as Json[]).find((item) => item.itemDescription === part);
  expect(line, `The six-line quote must carry ${part}.`).toBeTruthy();
  return line!;
}

async function createClientPo(page: Page, token: string, q: Json, poNumber: string, lines: Array<{ ref: string; line: Json; qty: number; price: number }>, keyTag = poNumber) {
  return status(await api(page, token, 'post', '/api/customer-awards/purchase-orders', {
    quoteId: q.id, commercialCaseId: q.commercialCaseId, customerId: q.customerId, currencyId: q.currencyId,
    externalPoNumber: poNumber, poDate: nowIso(), receivedOn: nowIso(), expectedVersion: 0,
    lines: lines.map((l) => ({
      externalLineReference: l.ref, productId: l.line.productId, description: l.line.itemDescription,
      orderedQuantity: l.qty, unitPrice: l.price, lineAmount: l.qty * l.price,
    })),
  }, key(`po-${keyTag}`)));
}

async function awardAndConfirm(page: Page, token: string, q: Json, po: Json, allocations: Array<{ poLineIndex: number; quoteItemId: number; qty: number }>, tag: string) {
  const projection = await jsonOk<Json>(await api(page, token, 'get', `/api/customer-awards/quote/${q.id}`));
  const award = await jsonOk<Json>(await api(page, token, 'post', '/api/customer-awards', {
    customerPurchaseOrderId: po.id, quoteId: q.id, expectedVersion: 0,
    customerPurchaseOrderExpectedVersion: po.version, quoteExpectedVersion: projection.quoteVersion,
    allocations: allocations.map((a) => ({ customerPurchaseOrderLineId: po.lines[a.poLineIndex].id, quoteItemId: a.quoteItemId, awardedQuantity: a.qty })),
  }, key(`award-${tag}`)));
  return jsonOk<Json>(await api(page, token, 'post', `/api/customer-awards/${award.id}/confirm`, { expectedVersion: award.version }, key(`confirm-${tag}`)));
}

async function matchView(page: Page, token: string, poId: number): Promise<Json> {
  return jsonOk<Json>(await api(page, token, 'get', `/api/customer-awards/purchase-orders/${poId}`));
}

async function shipmentsOf(page: Page, token: string, orderId: number): Promise<Json[]> {
  return jsonOk<Json[]>(await api(page, token, 'get', `/api/Shipment/order/${orderId}`));
}

async function createShipment(page: Page, token: string, orderId: number, items: Array<{ orderItemId: number; quantity: number }>, tag: string) {
  return status(await api(page, token, 'post', '/api/Shipment', {
    orderId, businessUnitId: tenantId(), statusId: requiredNumber('E2E_V2_SHIPMENT_STATUS_ID'),
    shipmentDate: nowIso(), carrier: 'Scenario carrier', shippingAddress: 'ABC Engineering acceptance dock', items,
  }, key(`ship-${tag}`)));
}

async function confirmDelivery(page: Page, token: string, shipmentId: number, lines: Array<{ shipmentItemId: number; acceptedQuantity: number; exceptionReasonCode: string | null }>, tag: string) {
  return status(await api(page, token, 'post', `/api/delivery/shipments/${shipmentId}/confirmation`, {
    receivedByName: 'Amira Cole', receivedOn: nowIso(), lines,
  }, key(`pod-${tag}`)));
}

async function invoice(page: Page, token: string, orderId: number, lines: Array<{ orderItemId: number; quantity: number }>, tag: string) {
  return status(await api(page, token, 'post', `/api/commercial-finance/orders/${orderId}/invoices`, { documentDate: null, dueDate: null, lines }, key(`inv-${tag}`)));
}

async function document(page: Page, token: string, id: number): Promise<Json> {
  return jsonOk<Json>(await api(page, token, 'get', `/api/commercial-finance/documents/${id}`));
}

async function pay(page: Page, token: string, order: Json, inv: Json, amount: number, reference: string, tag: string) {
  return status(await api(page, token, 'post', '/api/commercial-finance/payments', {
    customerId: order.customerId, commercialCaseId: order.commercialCaseId ?? null, currencyId: order.currencyId,
    paymentDate: inv.documentDate, method: 'BankTransfer', bankAccountId: requiredNumber('E2E_V2_BANK_ACCOUNT_ID'),
    amount, bankReference: reference, allocations: [{ receivableDocumentId: inv.id, amount }],
  }, key(`pay-${tag}`)));
}

// =====================================================================================
// 1. Send readiness names every missing thing before the dialog
// =====================================================================================

test('1a a quote with no currency is refused by readiness naming the currency; supplying it clears the blocker', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rfq = await jsonOk<Json>(await api(page, token, 'get', `/api/Rfq/${sixLineRfqId()}`));
  const prices: Record<string, number> = { 'CORE-ATP-100': 100, 'CORE-PARTIAL-200': 50, 'CORE-OOS-300': 600, 'CORE-INCOMING-400': 80, 'X-UNKNOWN-900': 40, 'FIELD-SERVICE': 300 };
  // Deliberately WITHOUT a currency: production's exact shape (two DRAFT quotes there, both NULL).
  const created = await jsonOk<Json>(await api(page, token, 'post', '/api/Quote', {
    rfqId: rfq.id, customerId: rfq.customerId, businessUnitId: tenantId(),
    validUntil: new Date(Date.now() + 40 * 86_400_000).toISOString(), quoteDate: nowIso(), headerRemarks: 'Scenario six-line quote',
    quoteItems: (rfq.rfqitems as Json[]).map((line) => ({
      rfqItemId: line.id, productId: line.productId, itemDescription: line.manufacturerPartNumber,
      quantity: line.quantity, unitPrice: prices[line.manufacturerPartNumber], totalAmount: line.quantity * prices[line.manufacturerPartNumber],
    })),
  }));
  state.sixLineId = created.id;
  expect(created.currencyId).toBeNull();

  const ready = await readiness(page, token, created.id);
  expect(ready.canSend).toBe(false);
  const incomplete = (ready.blockers as Json[]).find((b) => b.code === 'QUOTE_INCOMPLETE');
  expect(incomplete?.message).toContain('no currency');
  expect(blockerCodes(ready)).toContain('PRICE_ATTESTATION_REQUIRED');

  await page.goto(`/sales/quotes/view/${created.id}`);
  await expect(page.getByRole('button', { name: 'Send to customer' })).toBeDisabled();
  await expect(page.getByText(/this quote has no currency/i)).toBeVisible();

  // A second quote on the same RFQ is refused in words naming the first (it was a 500 before).
  const second = await status(await api(page, token, 'post', '/api/Quote', {
    rfqId: rfq.id, customerId: rfq.customerId, businessUnitId: tenantId(), validUntil: nowIso(), quoteDate: nowIso(),
    quoteItems: [{ rfqItemId: rfq.rfqitems[0].id, productId: rfq.rfqitems[0].productId, itemDescription: 'dup', quantity: 1, unitPrice: 1, totalAmount: 1 }],
  }));
  expect(second.status).toBe(409);
  expect(sentence(second.body)).toContain(created.quoteNo);

  expect((await updateQuote(page, token, created.id, { currencyId: currencyId() })).status).toBe(200);
  expect((await quote(page, token, created.id)).currencyId).toBe(currencyId());
  expect(blockerCodes(await readiness(page, token, created.id))).not.toContain('QUOTE_INCOMPLETE');
});

test('1b with no output tax rate configured the send is refused before the dialog and points at Commercial Policy', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const id = sendQuoteId();
  const policy = await jsonOk<Json>(await api(page, token, 'get', '/api/commercial-policy'));
  const cleared = await status(await api(page, token, 'put', '/api/commercial-policy', { clearOutputTaxRate: true, reason: 'Scenario: no output tax rate configured' }, key('policy-clear-tax')));
  expect(cleared.status, sentence(cleared.body)).toBe(200);
  // The rate is derived when a line is (re)priced: the price must move for the cleared policy to show.
  const startingPrice = Number((await quote(page, token, id)).quoteItems[0].unitPrice);
  expect((await updateQuote(page, token, id, { priceFor: () => startingPrice + 1 })).status).toBe(200);
  expect((await quote(page, token, id)).quoteItems[0].taxRatePercentApplied, 'A line priced with no tenant rate must carry no derived tax.').toBeNull();

  const ready = await readiness(page, token, id);
  expect(ready.canSend).toBe(false);
  const blocker = (ready.blockers as Json[]).find((b) => b.code === 'OUTPUT_TAX_NOT_DERIVED');
  expect(blocker?.setupPath).toBe('/setup/commercial-policy');
  await page.goto(`/sales/quotes/view/${id}`);
  await expect(page.getByRole('button', { name: 'Send to customer' })).toBeDisabled();
  await expect(page.getByRole('link', { name: /Open Setup → Commercial Policy/ })).toBeVisible();

  // The send itself refuses too (gate, not only advice), and nothing was queued.
  await jsonOk(await attest(page, token, id, 'Morgan Manager (no-tax scenario)'));
  const refused = await status(await send(page, token, id));
  expect(refused.status).toBe(409);
  expect((refused.body as Json).taxDerivationRequired).toBe(true);
  expect((await deliveryState(page, token, id)).inFlight).toBe(false);

  const restored = await status(await api(page, token, 'put', '/api/commercial-policy', { outputTaxRatePercent: policy.outputTaxRatePercent ?? 15, reason: 'Scenario: restore output tax rate' }, key('policy-restore-tax')));
  expect(restored.status, sentence(restored.body)).toBe(200);
  expect((await updateQuote(page, token, id, { priceFor: () => startingPrice })).status).toBe(200);
  expect((await quote(page, token, id)).quoteItems[0].taxRatePercentApplied).not.toBeNull();
});

test('1c readiness names PRICE_ATTESTATION_REQUIRED; attesting clears it and only the harness mail blocker remains', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const id = sendQuoteId();
  // 1b left an attestation whose snapshot no longer matches (the price moved back): the gate must say so.
  const before = await readiness(page, token, id);
  expect(before.canSend).toBe(false);
  const blocker = (before.blockers as Json[]).find((b) => b.code === 'PRICE_ATTESTATION_REQUIRED');
  expect(blocker?.message).toMatch(/price source|prices came from/i);

  await jsonOk(await attest(page, token, id, 'Morgan Manager'));
  const after = await readiness(page, token, id);
  expect(productBlockers(after)).toEqual([]);
  // canSend is false ONLY because this stack cannot transmit (DraftOnly guard, console platform sender).
  expect(blockerCodes(after).some((c) => HARNESS_MAIL_BLOCKERS.includes(c))).toBe(true);
  note('harness', `blockers after attestation: ${blockerCodes(after).join(',')} — canSend:true is unreachable on a DraftOnly stack`);
});

test('1d a price changed after attestation breaks the binding: the send is refused and nothing is queued', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const id = sendQuoteId();
  expect((await jsonOk<Json>(await api(page, token, 'get', `/api/Quote/${id}/price-attestation`))).satisfied).toBe(true);
  const attestedPrice = Number((await quote(page, token, id)).quoteItems[0].unitPrice);
  expect((await updateQuote(page, token, id, { priceFor: () => attestedPrice + 5 })).status).toBe(200);
  const stateAfter = await jsonOk<Json>(await api(page, token, 'get', `/api/Quote/${id}/price-attestation`));
  expect(stateAfter.satisfied).toBe(false);
  expect(stateAfter.supersededByPriceChange).toBe(true);
  expect(blockerCodes(await readiness(page, token, id))).toContain('PRICE_ATTESTATION_REQUIRED');

  const refused = await status(await send(page, token, id));
  expect(refused.status).toBe(409);
  expect((refused.body as Json).priceAttestationRequired).toBe(true);
  expect((await deliveryState(page, token, id)).inFlight).toBe(false);
  expect((await deliveryState(page, token, id)).outcome).toBeNull();
  // Re-attest at the new price so scenario 2 can send.
  await jsonOk(await attest(page, token, id, 'Morgan Manager'));
});

// =====================================================================================
// 2. Send: queued is not emailed; SENT only after the ledger row is sealed; no resend
// =====================================================================================

test('2a a send answers "queued", not "delivered"; a second send replays; the screen would say queued for delivery', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const id = sendQuoteId();
  expect(productBlockers(await readiness(page, token, id))).toEqual([]);

  const first = await status(await send(page, token, id));
  expect(first.status, sentence(first.body)).toBe(202);
  expect((first.body as Json).queuedForDelivery).toBe(true);
  expect((first.body as Json).delivered).toBe(false);
  expect((first.body as Json).replayed).toBe(false);
  const second = await status(await send(page, token, id));
  // The dispatcher polls every 5 s and dead-letters the withheld send on its first attempt, so
  // the replay window is narrow; either answer proves at-most-once, and which one is recorded.
  note('observation', `second send: HTTP ${second.status} ${JSON.stringify(second.body).slice(0, 160)}`);
  if (second.status === 202) expect((second.body as Json).replayed).toBe(true);
  else expect(second.status).toBe(409);

  // The screen's wording is derived from exactly these two flags (quoteService.describeQuoteSendOutcome),
  // covered by Frontend/src/api/services/quoteService.sendEmail.test.ts; the button itself is disabled on
  // this stack by the harness mail blocker, so the click cannot be driven here.
  await page.goto(`/sales/quotes/view/${id}`);
  await expect(page.getByRole('button', { name: /Send to customer|Send again/ })).toBeDisabled();
  await expect.poll(async () => (await deliveryState(page, token, id)).outcome, { timeout: 30_000 }).toBe('UNCERTAIN');
});

test('2b an unsealed delivery never makes the quote SENT; the tenant is told to check with the customer; retry is refused', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const id = sendQuoteId();
  const ready = await readiness(page, token, id);
  expect(ready.deliveryOutcome).toBe('UNCERTAIN');
  const blocker = (ready.blockers as Json[]).find((b) => b.code === 'DELIVERY_OUTCOME_UNCERTAIN');
  expect(blocker?.message).toMatch(/Check with the customer/);
  expect(blocker?.message).toMatch(/new revision/);
  // The ledger row was never sealed (no provider acceptance), so the status must not be SENT.
  const current = await quote(page, token, id);
  expect(current.statusCode).toBe('DRAFT');
  expect(current.sentOn).toBeNull();

  await page.goto(`/sales/quotes/view/${id}`);
  await expect(page.getByRole('button', { name: 'Send to customer' })).toBeDisabled();
  await expect(page.getByText(/Check with the customer/)).toBeVisible();

  const retry = await status(await send(page, token, id));
  expect(retry.status).toBe(409);
  expect(sentence(retry.body)).toMatch(/failed terminal state/);
  expect((retry.body as Json).errorCode).toMatch(/^DeliveryOutcomeUncertain/);
  expect((await deliveryState(page, token, id)).inFlight).toBe(false);
});

test('2c the reconcile cycle does not resend: after three dispatcher cycles the delivery is unchanged and the quote is still DRAFT', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const id = sendQuoteId();
  const before = await deliveryState(page, token, id);
  await page.waitForTimeout(16_000);
  const after = await deliveryState(page, token, id);
  expect(after).toEqual(before);
  expect((await quote(page, token, id)).statusCode).toBe('DRAFT');
  note('harness', 'A sealed row → SENT (dispatcher seals CompletedOn before FinalizeQuoteDeliveryAsync) cannot be reached on a DraftOnly stack; it is pinned by QuoteDeliveryDurabilityTests.');
});

// =====================================================================================
// 3. Revise: new revision, old one superseded, totals recomputed, attestation invalidated
// =====================================================================================

test('3a the dead draft can be revised — the only exit the readiness screen names — and the revision starts unattested', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const id = sendQuoteId();
  const info = await jsonOk<Json>(await api(page, token, 'get', `/api/Quote/${id}/revisions`));
  expect(info.canRevise, 'A draft whose delivery ended terminally must be revisable.').toBe(true);
  await page.goto(`/sales/quotes/view/${id}`);
  await expect(page.getByRole('button', { name: /new draft revision|^Revise$/ })).toBeVisible();

  const revised = await status(await api(page, token, 'post', `/api/Quote/${id}/revise`, {}));
  // KNOWN (fixed here by migration 20260905093000): on PostgreSQL the one-quote-per-RFQ index refused
  // every revision — "cannot be revised on this database … UX_Quotes_BusinessUnitID_RFQID".
  expect.soft(revised.status, sentence(revised.body)).toBe(201);
  if (revised.status !== 201) { note('blocked', `revise: HTTP ${revised.status} ${sentence(revised.body)}`); return; }
  const revision = revised.body as Json;
  const original = await quote(page, token, id);
  expect(revision.statusCode).toBe('DRAFT');
  expect(revision.revisionNo ?? revision.version).toBe(2);
  expect(revision.quoteNo).toBe(`${original.quoteNo}-R2`);
  expect(revision.totalAmount).toBe(original.totalAmount);
  expect((await readiness(page, token, revision.id)).deliveryOutcome).toBeNull();
  expect((await jsonOk<Json>(await api(page, token, 'get', `/api/Quote/${revision.id}/price-attestation`))).satisfied).toBe(false);
  expect(blockerCodes(await readiness(page, token, revision.id))).toContain('PRICE_ATTESTATION_REQUIRED');
  const oldInfo = await jsonOk<Json>(await api(page, token, 'get', `/api/Quote/${id}/revisions`));
  expect(oldInfo.supersededByQuoteId).toBe(revision.id);
  expect(oldInfo.canRevise).toBe(false);
});

test('3b revising a SENT quote supersedes it; a repriced revision recomputes totals and must be re-attested; the old one cannot be revised twice', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await jsonOk(await attest(page, token, state.sixLineId, 'Morgan Manager'));
  const sent = await markSentByHand(page, token, state.sixLineId, 'six-line-sent');
  expect(sent.statusCode).toBe('SENT');
  // A quote marked SENT by hand carries SentOn, so staleness and follow-up can start (it did not before).
  expect(sent.sentOn).not.toBeNull();
  expect(sent.daysSinceSent).toBe(0);

  const revised = await status(await api(page, token, 'post', `/api/Quote/${state.sixLineId}/revise`, {}));
  expect.soft(revised.status, sentence(revised.body)).toBe(201);
  if (revised.status !== 201) {
    note('blocked', `revise SENT: HTTP ${revised.status} ${sentence(revised.body)} — the Client PO scenarios continue against R1`);
    state.sixLineRevisionId = state.sixLineId;
    return;
  }
  const revision = revised.body as Json;
  state.sixLineRevisionId = revision.id;
  expect(revision.statusCode).toBe('DRAFT');
  expect(revision.totalAmount).toBe(sent.totalAmount);
  expect((await quote(page, token, state.sixLineId)).statusCode).toBe('SENT');

  // Reprice one line on R2: totals move, R1's do not, and R2 is unattested.
  const repriced = await updateQuote(page, token, revision.id, { priceFor: (item) => (item.itemDescription === 'FIELD-SERVICE' ? 350 : item.unitPrice) });
  expect(repriced.status, sentence(repriced.body)).toBe(200);
  const r2 = await quote(page, token, revision.id);
  expect(r2.totalAmount).toBeGreaterThan(sent.totalAmount);
  expect((await quote(page, token, state.sixLineId)).totalAmount).toBe(sent.totalAmount);
  expect((await jsonOk<Json>(await api(page, token, 'get', `/api/Quote/${revision.id}/price-attestation`))).satisfied).toBe(false);

  const again = await status(await api(page, token, 'post', `/api/Quote/${state.sixLineId}/revise`, {}));
  expect(again.status).toBe(409);
  expect(sentence(again.body)).toMatch(/already been revised/);
  const info = await jsonOk<Json>(await api(page, token, 'get', `/api/Quote/${state.sixLineId}/revisions`));
  expect(info.supersededByQuoteId).toBe(revision.id);
  await page.goto(`/sales/quotes/view/${revision.id}`);
  await expect(page.getByText(/Rev 2/)).toBeVisible();

  // A Client PO against the superseded R1 is refused; R2 must be attested and sent first.
  const stale = await createClientPo(page, token, await quote(page, token, state.sixLineId), `SCN-${RUN}-STALE`, [{ ref: '1', line: lineByPart(sent, 'CORE-ATP-100'), qty: 1, price: 100 }]);
  expect(stale.status).toBe(409);
  expect(sentence(stale.body)).toMatch(/latest quote revision/);
  await jsonOk(await attest(page, token, revision.id, 'Morgan Manager'));
  expect((await markSentByHand(page, token, revision.id, 'six-line-r2-sent')).statusCode).toBe('SENT');
});

// =====================================================================================
// 4. Below-floor: held for approval, visible in Approvals, reject keeps the draft, approve sends
// =====================================================================================

test('4a a line under the pricing floor is held for approval: it appears in Approvals, the requester cannot approve it, reject keeps the draft', async ({ page }) => {
  test.setTimeout(240_000);
  const token = await loginAs(page, 'manager');
  const id = mainQuoteId();
  // The floor comes from an approved sourcing award on the RFQ line (BelowFloorGuard: "No award, no floor").
  const bench = await jsonOk<Json>(await api(page, token, 'get', `/api/procurement/rfqs/${mainRfqId()}/workbench`));
  const award = (bench.awards as Json[]).find((a) => /APPROVED|CONVERTED/.test(a.status));
  if (!award) { note('harness', 'no sourcing award on the main RFQ: the below-floor hold cannot fire on this seed'); test.skip(); return; }
  const flooredLine = (bench.lines as Json[]).find((l) => l.id === award.rfqItemId)!;

  // The fixture seeds an OPEN customer-revision impact on this quote; the resolve verb must open the door.
  const resolved = await status(await api(page, token, 'post', `/api/Quote/${id}/revision-impact/resolve`, {}, key('resolve-impact')));
  expect(resolved.status).toBe(204);
  const readyAfterResolve = await readiness(page, token, id);
  // KNOWN (fixed here): the resolve wrote an audit event while readiness and the send read Status == OPEN.
  expect.soft(blockerCodes(readyAfterResolve), 'resolve must clear CUSTOMER_REVISION_UNRESOLVED').not.toContain('CUSTOMER_REVISION_UNRESOLVED');
  if (blockerCodes(readyAfterResolve).includes('CUSTOMER_REVISION_UNRESOLVED')) { note('blocked', 'the quote stays stale after resolve; the below-floor hold is unreachable on this build'); return; }

  expect((await updateQuote(page, token, id, { priceFor: (item) => (item.rfqItemId === flooredLine.id ? 1 : 25) })).status).toBe(200);
  await jsonOk(await attest(page, token, id, 'Morgan Manager'));
  expect(productBlockers(await readiness(page, token, id))).toEqual([]);
  note('finding', 'readiness has no BELOW_FLOOR advisory: the hold is only discovered after the price confirmation dialog');

  const held = await status(await send(page, token, id));
  expect(held.status, sentence(held.body)).toBe(409);
  expect((held.body as Json).queuedForApproval).toBe(true);
  expect((await deliveryState(page, token, id)).inFlight).toBe(false);
  expect((await quote(page, token, id)).statusCode).toBe('DRAFT');

  const pending = await jsonOk<Json[]>(await api(page, token, 'get', '/api/agent/approvals?status=pending'));
  const hold = pending.find((a) => a.toolName === 'approve_below_floor_quote');
  expect(hold, 'the hold must be listed for the manager').toBeTruthy();
  state.approvalId = hold!.id;
  await page.goto('/copilot/approvals');
  await expect(page.getByText(hold!.summary.slice(0, 40))).toBeVisible();

  // Editor (Member rank) sees only their own requests: nothing.
  const editor = await loginAs(page, 'editor');
  expect(await jsonOk<Json[]>(await api(page, editor, 'get', '/api/agent/approvals?status=pending'))).toEqual([]);
  expect((await api(page, editor, 'post', `/api/agent/approvals/${hold!.id}/approve`)).status()).toBe(403);

  const manager = await loginAs(page, 'manager');
  const self = await status(await api(page, manager, 'post', `/api/agent/approvals/${hold!.id}/approve`));
  expect(self.status).toBe(409);
  expect(sentence(self.body)).toMatch(/Segregation of duties/);
  const rejected = await status(await api(page, manager, 'post', `/api/agent/approvals/${hold!.id}/reject`));
  expect(rejected.status).toBe(200);
  expect((rejected.body as Json).status).toBe('rejected');
  note('finding', 'POST /api/agent/approvals/{id}/reject takes no reason: the rejection is recorded as "Rejected by human reviewer." with nothing the rep can act on');
  expect((await quote(page, manager, id)).statusCode).toBe('DRAFT');
  expect((await deliveryState(page, manager, id)).inFlight).toBe(false);
  const twice = await status(await api(page, manager, 'post', `/api/agent/approvals/${hold!.id}/reject`));
  expect(twice.status).toBe(409);
  expect(sentence(twice.body)).toMatch(/not pending/);
});

test('4b a second manager approves the re-raised hold and the send goes out (queued on this stack)', async ({ page }) => {
  const manager = await loginAs(page, 'manager');
  const id = mainQuoteId();
  if (!state.approvalId) { note('blocked', 'no hold was raised in 4a'); test.skip(); return; }
  const held = await status(await send(page, manager, id));
  expect(held.status).toBe(409);
  expect((held.body as Json).queuedForApproval).toBe(true);
  const pending = await jsonOk<Json[]>(await api(page, manager, 'get', '/api/agent/approvals?status=pending'));
  const hold = pending.find((a) => a.toolName === 'approve_below_floor_quote')!;
  expect(hold).toBeTruthy();

  const owner = await loginAsOwner(page);
  const approved = await status(await api(page, owner, 'post', `/api/agent/approvals/${hold.id}/approve`));
  expect(approved.status, sentence(approved.body)).toBe(200);
  expect((approved.body as Json).status).toBe('executed');
  await expect.poll(async () => { const s = await deliveryState(page, owner, id); return s.inFlight || s.outcome !== null; }, { timeout: 30_000 }).toBe(true);
  // KNOWN (fixed here): the executed decision was never saved — the send's execution strategy
  // cleared the change tracker under the loaded approval — so the hold stayed "pending" and every
  // further Approve ran the send again.
  const executed = await jsonOk<Json[]>(await api(page, owner, 'get', '/api/agent/approvals?status=executed'));
  expect.soft(executed.map((a) => a.id), 'the decision must be recorded as executed').toContain(hold.id);
  const again = await status(await api(page, owner, 'post', `/api/agent/approvals/${hold.id}/approve`));
  expect.soft(again.status, 'a decided hold must not be approvable again').toBe(409);
  if (again.status === 409) expect(sentence(again.body)).toMatch(/not pending/);
});

// =====================================================================================
// 5. Order: from the quote it carries the quote's currency; by hand it carries none
// =====================================================================================

/** Lets a later segment run on its own (--grep) by reading the ids the earlier tests persisted. */
async function recoverState(page: Page, token: string): Promise<void> {
  if (!state.sixLineRevisionId) {
    const rows = await jsonOk<any>(await api(page, token, 'get', '/api/Quote?pageSize=200'))
      .then((r: any) => (Array.isArray(r) ? r : r.items ?? []) as Json[]);
    const chain = rows.filter((q) => q.rfqId === sixLineRfqId()).sort((a, b) => (b.revisionNo ?? 1) - (a.revisionNo ?? 1));
    if (chain.length === 0) throw new Error('Scenario 1a/3b did not run: no quote on the six-line RFQ.');
    state.sixLineRevisionId = chain[0].id;
    state.sixLineId = chain[chain.length - 1].id;
  }
  if (!state.orderId) {
    const orders = await jsonOk<Json[]>(await api(page, token, 'get', '/api/Order'));
    const order = orders.find((o) => o.rfqId === sixLineRfqId() && o.orderNo?.startsWith('SO-'));
    if (order) {
      state.orderId = order.id;
      const items = order.items as Json[];
      state.orderItemAtp = items.find((i) => i.partNo === 'CORE-ATP-100' || i.productId === 1)?.id ?? items[0].id;
      state.orderItemPartial = items.find((i) => i.partNo === 'CORE-PARTIAL-200' || i.productId === 2)?.id ?? items[1].id;
      const shipments = await shipmentsOf(page, token, order.id);
      state.shipmentShort = shipments.find((sh) => sh.items.some((i: Json) => i.orderItemId === state.orderItemAtp))?.id ?? 0;
      state.shipmentRace = shipments.find((sh) => sh.items.some((i: Json) => i.orderItemId === state.orderItemPartial))?.id ?? 0;
      if (state.shipmentShort) {
        const proof = await api(page, token, 'get', `/api/delivery/shipments/${state.shipmentShort}/confirmation`);
        if (proof.ok()) {
          const line = ((await proof.json()).lines as Json[])[0];
          state.proofLineShort = line.id;
          state.acceptedAtp = line.acceptedQuantity;
        }
      }
      const finance = await loginAsFinance(page);
      const documents = await jsonOk<Json[]>(await api(page, finance, 'get', `/api/commercial-finance/documents?customerId=${order.customerId}`));
      state.invoiceId = documents.find((d) => d.orderId === order.id && d.status === 'Issued')?.id ?? 0;
      await loginAs(page, 'manager');
    }
  }
}

test('5a an exact Client PO on the latest revision converts to an order that carries the quote currency', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await recoverState(page, token);
  const q = await quote(page, token, state.sixLineRevisionId);
  expect(q.statusCode).toBe('SENT');
  if (state.orderId) {
    // Retained database (segment re-run): the PO was already awarded and converted; check what it left.
    const existing = await jsonOk<Json>(await api(page, token, 'get', `/api/Order/${state.orderId}`));
    expect(existing.currencyId).toBe(q.currencyId);
    note('harness', `retained database: order ${existing.orderNo} already exists for the six-line quote; PO capture not repeated`);
    return;
  }
  const l1 = lineByPart(q, 'CORE-ATP-100');
  const l2 = lineByPart(q, 'CORE-PARTIAL-200');
  const l3 = lineByPart(q, 'CORE-OOS-300');
  const poNumber = `SCN-${RUN}-EXACT`;
  const created = await createClientPo(page, token, q, poNumber, [
    { ref: '1', line: l1, qty: l1.quantity, price: l1.unitPrice },
    { ref: '2', line: l2, qty: l2.quantity, price: l2.unitPrice },
    { ref: '3', line: l3, qty: l3.quantity, price: l3.unitPrice },
  ]);
  expect(created.status, sentence(created.body)).toBe(201);
  const po = created.body as Json;
  const confirmed = await awardAndConfirm(page, token, q, po, [
    { poLineIndex: 0, quoteItemId: l1.id, qty: l1.quantity },
    { poLineIndex: 1, quoteItemId: l2.id, qty: l2.quantity },
    { poLineIndex: 2, quoteItemId: l3.id, qty: l3.quantity },
  ], 'exact');
  const match = await matchView(page, token, po.id);
  expect(match.header.matchOutcome).toBe('EXACT_ACCEPTANCE');
  expect((match.lines as Json[]).every((line) => line.matchStatus === 'EXACT_MATCH' && line.differences.length === 0)).toBe(true);

  const converted = await status(await api(page, token, 'post', `/api/customer-awards/${confirmed.id}/convert-to-order`, { expectedVersion: confirmed.version }, key('convert-exact')));
  expect(converted.status, sentence(converted.body)).toBe(200);
  const order = await jsonOk<Json>(await api(page, token, 'get', `/api/Order/${(converted.body as Json).id}`));
  state.orderId = order.id;
  expect(order.currencyId).toBe(q.currencyId);
  expect(order.currencyCode).toBe(q.currencyCode);
  expect(order.items).toHaveLength(3);
  // The order read DTO does not carry customerAwardId; the conversion response does.
  expect((converted.body as Json).customerAwardId).toBe(confirmed.id);
  state.orderItemAtp = (order.items as Json[]).find((i) => i.productId === l1.productId)!.id;
  state.orderItemPartial = (order.items as Json[]).find((i) => i.productId === l2.productId)!.id;
  note('observation', `converted order status: ${order.status}`);

  // A new key with the same PO number: the duplicate-number rule, not the idempotency replay, must answer.
  const same = await createClientPo(page, token, q, poNumber, [{ ref: '1', line: l1, qty: 1, price: l1.unitPrice }], `${poNumber}-again`);
  expect(same.status).toBe(409);
  expect(sentence(same.body)).toMatch(/already exists/);
  await page.goto('/sales/client-pos');
  await expect(page.getByRole('heading', { name: 'Client PO Inbox' })).toBeVisible();
  await expect(page.getByText(poNumber)).toBeVisible();
});

test('5b a manual order names its enquiry but has no way to state a currency, and finance is refused in words when it reaches the invoice', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const noCase = await status(await api(page, token, 'post', '/api/Order', {
    customerId: requiredNumber('E2E_CORE_CUSTOMER_ID'), businessUnitId: tenantId(), orderDate: nowIso(),
    items: [{ productId: requiredNumber('E2E_V2_CLIENT_PO_EXACT_PRODUCT_ID'), quantity: 1, unitPrice: 100, discount: 0, taxAmount: 0 }],
  }, key('manual-no-case')));
  expect(noCase.status).toBe(400);
  expect(sentence(noCase.body)).toMatch(/originate from an inquiry/);

  const created = await status(await api(page, token, 'post', '/api/Order', {
    rfqId: sixLineRfqId(), customerId: requiredNumber('E2E_CORE_CUSTOMER_ID'), businessUnitId: tenantId(), orderDate: nowIso(),
    warehouseId: requiredNumber('E2E_CORE_PRIMARY_WAREHOUSE_ID'), notes: 'Scenario manual order',
    items: [{ productId: requiredNumber('E2E_V2_CLIENT_PO_EXACT_PRODUCT_ID'), quantity: 1, unitPrice: 100, discount: 0, taxAmount: 0, warehouseId: requiredNumber('E2E_CORE_PRIMARY_WAREHOUSE_ID') }],
  }, key('manual-order')));
  expect(created.status, sentence(created.body)).toBe(201);
  const manual = created.body as Json;
  // KNOWN GAP (being fixed on fix/order-currency, not here): CreateOrderPage has no currency field and
  // the API accepts an order without one. Recorded, not asserted.
  note('observation', `manual order ${manual.orderNo} currencyId=${manual.currencyId}`);
  // The screen's door is shut — /sales/orders/create says customer orders start from a Client PO —
  // while the API's door (POST /api/Order with an rfqId, no currency) is open. Recorded for G1.
  await page.goto('/sales/orders/create');
  await expect(page.getByRole('heading', { name: /Customer orders start from an accepted purchase order/i })).toBeVisible();
  note('observation', `manual order screen: refused in words; currency control count: ${await page.getByLabel(/currency/i).count()}`);

  const statuses = await jsonOk<Json>(await api(page, token, 'get', '/api/SetupMaster?setupType=OrderStatus&pageSize=50'));
  const confirmedId = ((statuses.items ?? statuses) as Json[]).find((s) => s.setupCode === 'CONFIRMED')!.setupId;
  expect((await status(await api(page, token, 'put', `/api/Order/${manual.id}`, { statusId: confirmedId, modifiedBy: 'scenario' }))).status).toBe(200);
  const finance = await loginAsFinance(page);
  const refused = await invoice(page, finance, manual.id, [], 'manual');
  if (manual.currencyId == null) {
    expect(refused.status).toBe(409);
    expect(sentence(refused.body)).toMatch(/has no currency/);
    expect(sentence(refused.body)).toContain(manual.orderNo);
  } else {
    note('observation', `the manual order carried a currency (${manual.currencyId}); invoice: HTTP ${refused.status}`);
  }
});

test('5c direct quote-to-order is retired: from-quote answers 409 and creates nothing', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await recoverState(page, token);
  const before = (await jsonOk<Json[]>(await api(page, token, 'get', '/api/Order'))).length;
  for (const attempt of [1, 2]) {
    const refused = await status(await api(page, token, 'post', `/api/Order/from-quote/${state.sixLineRevisionId}`, {}, key(`from-quote-${attempt}`)));
    expect(refused.status).toBe(409);
    expect(sentence(refused.body)).toMatch(/convert-to-order/);
  }
  expect((await jsonOk<Json[]>(await api(page, token, 'get', '/api/Order'))).length).toBe(before);
});

// =====================================================================================
// 6. Shipment → proof of delivery by the right role only; double confirmation refused
// =====================================================================================

test('6a over-shipping is refused; two despatches are created and the by-order read carries their lines', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await recoverState(page, token);
  const order = await jsonOk<Json>(await api(page, token, 'get', `/api/Order/${state.orderId}`));
  const atp = (order.items as Json[]).find((i) => i.id === state.orderItemAtp)!;
  const allocation = await jsonOk<Json>(await api(page, token, 'post', `/api/Order/${order.id}/allocate`));
  note('observation', `allocation: fullyAllocated=${allocation.fullyAllocated} shortages=${allocation.hasShortages}`);
  const over = await createShipment(page, token, order.id, [{ orderItemId: atp.id, quantity: atp.quantity + 1 }], 'over');
  expect(over.status).toBe(409);
  expect(sentence(over.body)).toMatch(/exceeds the remaining quantity/);

  const first = await createShipment(page, token, order.id, [{ orderItemId: atp.id, quantity: atp.quantity }], 'atp');
  expect(first.status, sentence(first.body)).toBe(201);
  state.shipmentShort = (first.body as Json).id;
  const second = await createShipment(page, token, order.id, [{ orderItemId: state.orderItemPartial, quantity: 2 }], 'partial');
  expect(second.status, sentence(second.body)).toBe(201);
  state.shipmentRace = (second.body as Json).id;

  const listed = await shipmentsOf(page, token, order.id);
  expect(listed.map((s) => s.id).sort()).toEqual([state.shipmentShort, state.shipmentRace].sort());
  // The by-order read and the list carry their lines (they answered items: [] before the fix).
  expect(listed.every((s) => s.items.length === 1 && s.deliveryStatus === 'DISPATCHED')).toBe(true);
  const all = await jsonOk<Json[]>(await api(page, token, 'get', '/api/Shipment'));
  expect(all.find((s) => s.id === state.shipmentShort)!.items).toHaveLength(1);
  // Two lines are still open, so the order screen correctly keeps offering the next despatch (it sums
  // the by-order lines — the read that answered items: [] before F7).
  await page.goto(`/sales/orders/${order.id}`);
  await expect(page.getByRole('button', { name: /Create next shipment/i })).toBeVisible();
});

test('6b finance and denied are refused with a sentence; the manager records a short POD through the screen; a second POD is refused; the decision needs both permissions', async ({ page }) => {
  const manager = await loginAs(page, 'manager');
  await recoverState(page, manager);
  const shipment = await jsonOk<Json>(await api(page, manager, 'get', `/api/Shipment/${state.shipmentShort}`));
  const item = shipment.items[0];
  const full = [{ shipmentItemId: item.id, acceptedQuantity: item.quantity, exceptionReasonCode: null }];

  const finance = await loginAsFinance(page);
  const byFinance = await confirmDelivery(page, finance, shipment.id, full, 'finance');
  expect(byFinance.status).toBe(403);
  expect(sentence(byFinance.body)).toMatch(/permission/);
  const denied = await loginAs(page, 'denied');
  expect((await confirmDelivery(page, denied, shipment.id, full, 'denied')).status).toBe(403);

  const token = await loginAs(page, 'manager');
  const unreasoned = await confirmDelivery(page, token, shipment.id, [{ shipmentItemId: item.id, acceptedQuantity: item.quantity - 1, exceptionReasonCode: null }], 'no-reason');
  expect(unreasoned.status).toBe(400);
  expect(sentence(unreasoned.body)).toMatch(/must state why/);

  await page.goto(`/sales/shipments/${shipment.id}`);
  await page.getByRole('button', { name: 'Record proof of delivery' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel(/Received by \(name\)/).fill('Amira Cole');
  const row = dialog.getByRole('row').filter({ hasText: item.productName ?? '' });
  await row.getByRole('spinbutton').fill(String(item.quantity - 1));
  await row.getByRole('combobox').click();
  await page.getByRole('option', { name: /Short shipment/ }).click();
  await dialog.getByRole('button', { name: 'Record proof of delivery' }).click();
  await expect(page.getByText('PROOF OF DELIVERY', { exact: true })).toBeVisible({ timeout: 20_000 });

  const proof = await jsonOk<Json>(await api(page, token, 'get', `/api/delivery/shipments/${shipment.id}/confirmation`));
  expect(proof.outcome).toBe('DELIVERY_EXCEPTION');
  const line = (proof.lines as Json[])[0];
  expect(line.acceptedQuantity).toBe(item.quantity - 1);
  expect(line.refusedQuantity).toBe(1);
  expect(line.exceptionReasonCode).toBe('SHORT_SHIPMENT');
  state.proofLineShort = line.id;
  state.acceptedAtp = line.acceptedQuantity;
  expect((await jsonOk<Json>(await api(page, token, 'get', `/api/Shipment/${shipment.id}`))).deliveryStatus).toBe('DELIVERY_EXCEPTION');

  // Double confirmation: a new key answers 409 in words; so does the editor after the fact.
  const again = await confirmDelivery(page, token, shipment.id, full, 'again');
  expect(again.status).toBe(409);
  expect(sentence(again.body)).toMatch(/cannot be confirmed received|already carries a proof/);
  const editor = await loginAs(page, 'editor');
  expect((await confirmDelivery(page, editor, shipment.id, full, 'editor-late')).status).toBe(409);

  // The commercial decision is gated on Shipments:Edit AND Orders:Edit.
  const financeAgain = await loginAsFinance(page);
  expect((await status(await api(page, financeAgain, 'post', `/api/delivery/shortfalls/${line.id}/decision`, { decision: 'CREDIT', reason: 'no' }))).status).toBe(403);
  const editorAgain = await loginAs(page, 'editor');
  const decided = await status(await api(page, editorAgain, 'post', `/api/delivery/shortfalls/${line.id}/decision`, { decision: 'RESUPPLY', reason: 'Send the missing unit against the same order line.' }));
  expect(decided.status, sentence(decided.body)).toBe(200);
  expect((decided.body as Json).commercialDecision).toBe('RESUPPLY');
  const managerAgain = await loginAs(page, 'manager');
  const twice = await status(await api(page, managerAgain, 'post', `/api/delivery/shortfalls/${line.id}/decision`, { decision: 'CREDIT', reason: 'Changed my mind.' }));
  expect(twice.status).toBe(409);
  expect(sentence(twice.body)).toMatch(/append-only/);
  await page.goto(`/sales/shipments/${shipment.id}`);
  await expect(page.getByText(/RESUPPLY/)).toBeVisible();
});

// =====================================================================================
// 7. Invoice from the delivered order → payments
// =====================================================================================

test('7a finance invoices the accepted quantity in the order currency; manager is refused; over-accepted is refused; issue is once and replays', async ({ page }) => {
  const manager = await loginAs(page, 'manager');
  await recoverState(page, manager);
  const order = await jsonOk<Json>(await api(page, manager, 'get', `/api/Order/${state.orderId}`));
  expect((await invoice(page, manager, order.id, [{ orderItemId: state.orderItemAtp, quantity: 1 }], 'manager')).status).toBe(403);

  const finance = await loginAsFinance(page);
  const draft = await invoice(page, finance, order.id, [{ orderItemId: state.orderItemAtp, quantity: state.acceptedAtp }], 'atp');
  // KNOWN (fixed here): the order raised from the confirmed Client PO is DRAFT, locked by its first
  // shipment, and moved to DELIVERED only when EVERY line is fully accepted — so finance was refused
  // "must be confirmed, completed, shipped, or backed by an accepted customer quote" for ever.
  expect.soft(draft.status, sentence(draft.body)).toBe(201);
  if (draft.status !== 201) { note('blocked', `invoice: HTTP ${draft.status} ${sentence(draft.body)} — order status ${order.status}`); return; }
  let inv = draft.body as Json;
  expect(inv.currencyId).toBe(order.currencyId);
  expect(inv.status).toBe('Draft');
  state.invoiceId = inv.id;

  const overAccepted = await invoice(page, finance, order.id, [{ orderItemId: state.orderItemAtp, quantity: state.acceptedAtp + 1 }], 'over-accepted');
  expect(overAccepted.status).toBe(409);
  expect(sentence(overAccepted.body)).toMatch(/accepted/);

  inv = await jsonOk<Json>(await api(page, finance, 'post', `/api/commercial-finance/documents/${inv.id}/issue`, { expectedVersion: inv.version }));
  expect(inv.status).toBe('Issued');
  expect(inv.documentNumber).toMatch(/^INV-\d{4}-\d{6}$/);
  const again = await status(await api(page, finance, 'post', `/api/commercial-finance/documents/${inv.id}/issue`, { expectedVersion: inv.version }));
  expect(again.status).toBe(200);
  expect((again.body as Json).documentNumber).toBe(inv.documentNumber);
  // A second draft for the same accepted quantity can be drafted but not issued: the cap holds at issue.
  const dup = await invoice(page, finance, order.id, [{ orderItemId: state.orderItemAtp, quantity: state.acceptedAtp }], 'atp-dup');
  if (dup.status === 201) {
    const dupIssue = await status(await api(page, finance, 'post', `/api/commercial-finance/documents/${(dup.body as Json).id}/issue`, { expectedVersion: (dup.body as Json).version }));
    expect(dupIssue.status).toBe(409);
    note('observation', `duplicate draft issue: ${sentence(dupIssue.body)}`);
  } else {
    note('observation', `duplicate draft: HTTP ${dup.status} ${sentence(dup.body)}`);
  }
  const documents = await jsonOk<Json[]>(await api(page, finance, 'get', `/api/commercial-finance/documents?customerId=${order.customerId}`));
  expect(documents.filter((d) => d.orderId === order.id && d.status === 'Issued')).toHaveLength(1);
});

test('7b a partial payment leaves the balance; overpayment is refused; the balance is settled through the screen; a further payment is refused', async ({ page }) => {
  if (!state.invoiceId) { note('blocked', 'no invoice from 7a'); test.skip(); return; }
  const manager = await loginAs(page, 'manager');
  await recoverState(page, manager);
  const order = await jsonOk<Json>(await api(page, manager, 'get', `/api/Order/${state.orderId}`));
  const finance = await loginAsFinance(page);
  let inv = await document(page, finance, state.invoiceId);
  const total = inv.totalAmount;

  const over = await pay(page, finance, order, inv, total + 100, `SCN-${RUN}-OVER`, 'over');
  expect(over.status).toBe(409);
  expect(sentence(over.body)).toMatch(/exceeds/);

  const half = Math.round((total / 2) * 100) / 100;
  const partial = await pay(page, finance, order, inv, half, `SCN-${RUN}-PART`, 'part');
  expect(partial.status, sentence(partial.body)).toBe(201);
  inv = await document(page, finance, state.invoiceId);
  expect(inv.outstandingAmount).toBeCloseTo(total - half, 2);
  expect(inv.status).toBe('Issued');
  note('observation', 'there is no PAID document status: settlement is outstandingAmount 0 and absence from AR open items');

  await page.goto(`/sales/finance?documentId=${inv.id}`);
  await expect(page.getByRole('heading', { name: 'Accounts Receivable' })).toBeVisible();
  await page.getByRole('button', { name: `Record payment for ${inv.documentNumber}` }).click();
  const dialog = page.getByRole('dialog', { name: 'Record payment' });
  await dialog.getByLabel('Bank reference').fill(`SCN-${RUN}-CASH`);
  const posted = page.waitForResponse((r) => r.request().method() === 'POST' && r.url().endsWith('/api/commercial-finance/payments'));
  await dialog.getByRole('button', { name: 'Post payment' }).click();
  expect((await posted).status()).toBe(201);
  await expect(page.getByText('Payment posted and allocated')).toBeVisible();

  inv = await document(page, finance, state.invoiceId);
  expect(inv.outstandingAmount).toBe(0);
  const open = await jsonOk<Json[]>(await api(page, finance, 'get', '/api/commercial-finance/ar/open-items'));
  expect(open.some((item) => item.documentId === inv.id)).toBe(false);
  const extra = await pay(page, finance, order, inv, 1, `SCN-${RUN}-EXTRA`, 'extra');
  expect(extra.status).toBe(409);
  expect(sentence(extra.body)).toMatch(/exceeds/);
  const payments = await jsonOk<Json[]>(await api(page, finance, 'get', `/api/commercial-finance/payments?customerId=${order.customerId}`));
  expect(payments.filter((p) => p.bankReference.startsWith(`SCN-${RUN}-`))).toHaveLength(2);
  await page.goto(`/sales/finance?documentId=${inv.id}`);
  await expect(page.getByRole('row').filter({ hasText: inv.documentNumber })).toContainText('0.00');
});

// =====================================================================================
// 8. Concurrency: exactly one wins
// =====================================================================================

test('8a two operators confirm the same delivery at once: exactly one proof is recorded, the other is refused in words', async ({ page }) => {
  const manager = await loginAs(page, 'manager');
  await recoverState(page, manager);
  const editor = await loginAs(page, 'editor');
  const shipment = await jsonOk<Json>(await api(page, manager, 'get', `/api/Shipment/${state.shipmentRace}`));
  const item = shipment.items[0];
  const lines = [{ shipmentItemId: item.id, acceptedQuantity: item.quantity, exceptionReasonCode: null }];
  const [a, b] = await Promise.all([
    confirmDelivery(page, manager, shipment.id, lines, 'race-manager'),
    confirmDelivery(page, editor, shipment.id, lines, 'race-editor'),
  ]);
  const statuses = [a.status, b.status].sort();
  expect(statuses, `${sentence(a.body)} | ${sentence(b.body)}`).toEqual([201, 409]);
  const loser = a.status === 409 ? a : b;
  expect(sentence(loser.body)).toMatch(/already carries a proof|cannot be confirmed received|confirmed concurrently|conflict/i);
  const proof = await jsonOk<Json>(await api(page, manager, 'get', `/api/delivery/shipments/${shipment.id}/confirmation`));
  expect(proof.outcome).toBe('DELIVERED');
  expect(proof.lines).toHaveLength(1);
  note('matrix', `POD race: manager ${a.status}, editor ${b.status}`);
});

test('8b two operators issue the same invoice at once: one document number, one Issued row', async ({ page }) => {
  const manager = await loginAs(page, 'manager');
  await recoverState(page, manager);
  const finance = await loginAsFinance(page);
  const order = await jsonOk<Json>(await api(page, manager, 'get', `/api/Order/${state.orderId}`));
  const draft = await invoice(page, finance, order.id, [{ orderItemId: state.orderItemPartial, quantity: 2 }], 'partial-line');
  expect.soft(draft.status, sentence(draft.body)).toBe(201);
  if (draft.status !== 201) { note('blocked', `invoice for the race: ${sentence(draft.body)}`); return; }
  const doc = draft.body as Json;
  const issue = async () => status(await api(page, finance, 'post', `/api/commercial-finance/documents/${doc.id}/issue`, { expectedVersion: doc.version }));
  const [a, b] = await Promise.all([issue(), issue()]);
  note('matrix', `issue race: ${a.status} ${(a.body as Json).documentNumber ?? sentence(a.body)} | ${b.status} ${(b.body as Json).documentNumber ?? sentence(b.body)}`);
  expect([a.status, b.status].filter((s) => s === 200).length).toBeGreaterThanOrEqual(1);
  expect([a.status, b.status].every((s) => s === 200 || s === 409)).toBe(true);
  const numbers = new Set([a, b].filter((r) => r.status === 200).map((r) => (r.body as Json).documentNumber));
  expect(numbers.size).toBe(1);
  const issued = await document(page, finance, doc.id);
  expect(issued.status).toBe('Issued');
  const documents = await jsonOk<Json[]>(await api(page, finance, 'get', `/api/commercial-finance/documents?customerId=${order.customerId}`));
  expect(documents.filter((d) => d.id === doc.id)).toHaveLength(1);
});

// =====================================================================================
// 9. Role boundaries and the other tenant, verb by verb
// =====================================================================================

test('9 each verb answers the right boundary to editor, finance, denied and the other tenant', async ({ page }) => {
  const manager = await loginAs(page, 'manager');
  await recoverState(page, manager);
  const quoteId = sendQuoteId();
  const order = await jsonOk<Json>(await api(page, manager, 'get', `/api/Order/${state.orderId}`));
  const shipmentId = state.shipmentShort;
  const results: string[] = [];
  const record = async (label: string, response: APIResponse, allowed: number[]) => {
    results.push(`${label}: ${response.status()}`);
    expect(allowed, `${label} answered ${response.status()}: ${await response.text()}`).toContain(response.status());
  };

  const editor = await loginAs(page, 'editor');
  // Member rank sees only the commercial cases assigned to it (CommercialAccessScope): the fixture's
  // send quote belongs to another rep's lead, so the editor is answered 404 on it — scope, not RBAC.
  await record('editor reads quote (scoped)', await api(page, editor, 'get', `/api/Quote/${quoteId}`), [200, 404]);
  await record('editor sends quote (dead delivery / scoped)', await send(page, editor, quoteId), [404, 409]);
  await record('editor revises (scoped)', await api(page, editor, 'post', `/api/Quote/${quoteId}/revise`, {}), [201, 404, 409]);
  await record('editor reads the six-line quote it can see', await api(page, editor, 'get', `/api/Quote/${state.sixLineRevisionId}`), [200, 404]);
  await record('editor invoices', await api(page, editor, 'post', `/api/commercial-finance/orders/${order.id}/invoices`, { documentDate: null, dueDate: null, lines: [] }, key('editor-inv')), [403]);
  await record('editor posts payment', await api(page, editor, 'post', '/api/commercial-finance/payments', {}, key('editor-pay')), [403]);
  await record('editor reads AR documents', await api(page, editor, 'get', `/api/commercial-finance/documents?customerId=${order.customerId}`), [403]);
  await record('editor approves a hold', await api(page, editor, 'get', '/api/agent/approvals?status=pending'), [200]);

  const finance = await loginAsFinance(page);
  await record('finance sends quote', await send(page, finance, quoteId), [403]);
  await record('finance reads readiness', await api(page, finance, 'get', `/api/Quote/${quoteId}/send-readiness`), [403]);
  await record('finance creates shipment', await api(page, finance, 'post', '/api/Shipment', { orderId: order.id, statusId: 1, items: [] }, key('finance-ship')), [403]);
  await record('finance confirms delivery', await api(page, finance, 'post', `/api/delivery/shipments/${shipmentId}/confirmation`, { receivedByName: 'x', receivedOn: nowIso(), lines: [] }, key('finance-pod')), [403]);
  // FINDING: finance (Member rank, Orders:View) is answered 404 on the order it is invoicing — the
  // order read is commercial-scoped to the assigned rep while the invoice endpoints are not.
  await record('finance reads order (view-only, scoped)', await api(page, finance, 'get', `/api/Order/${order.id}`), [200, 404]);
  await record('finance allocates order', await api(page, finance, 'post', `/api/Order/${order.id}/allocate`), [403, 404]);
  await record('finance reads AR', await api(page, finance, 'get', `/api/commercial-finance/documents?customerId=${order.customerId}`), [200]);

  const denied = await loginAs(page, 'denied');
  await record('denied reads quote', await api(page, denied, 'get', `/api/Quote/${quoteId}`), [403]);
  await record('denied reads order', await api(page, denied, 'get', `/api/Order/${order.id}`), [403]);
  await record('denied reads shipment', await api(page, denied, 'get', `/api/Shipment/${shipmentId}`), [403]);
  await record('denied reads AR', await api(page, denied, 'get', '/api/commercial-finance/documents'), [403]);
  await record('denied reads client POs', await api(page, denied, 'get', '/api/customer-awards/purchase-orders'), [403]);

  const other = await loginAsOtherTenant(page);
  // The other tenant's user holds Leads and Orders:View only: a quote verb is 403 (no module grant)
  // before tenant scoping can answer 404; the order, which it may view in its own tenant, is 404.
  await record('other tenant reads quote', await api(page, other, 'get', `/api/Quote/${quoteId}`), [403, 404]);
  await record('other tenant reads readiness', await api(page, other, 'get', `/api/Quote/${quoteId}/send-readiness`), [403, 404]);
  await record('other tenant revises', await api(page, other, 'post', `/api/Quote/${quoteId}/revise`, {}), [403, 404]);
  await record('other tenant reads order (has Orders:View)', await api(page, other, 'get', `/api/Order/${order.id}`), [404]);
  await record('other tenant reads shipment', await api(page, other, 'get', `/api/Shipment/${shipmentId}`), [403, 404]);
  await record('other tenant confirms delivery', await api(page, other, 'post', `/api/delivery/shipments/${shipmentId}/confirmation`, { receivedByName: 'x', receivedOn: nowIso(), lines: [] }, key('other-pod')), [403, 404]);
  await record('other tenant reads AR document', await api(page, other, 'get', `/api/commercial-finance/documents/${state.invoiceId || 1}`), [403, 404]);
  await record('other tenant reads client PO', await api(page, other, 'get', `/api/customer-awards/purchase-orders`), [403, 404]);
  note('matrix', results.join(' | '));

  await loginAs(page, 'manager');
  await page.goto(`/sales/quotes/view/${quoteId}`);
  const sendButton = page.getByRole('button', { name: /Send to customer|Send again/ });
  await expect(sendButton).toBeDisabled();
  await expect(sendButton).toHaveAttribute('title', /.{20,}/);
});
