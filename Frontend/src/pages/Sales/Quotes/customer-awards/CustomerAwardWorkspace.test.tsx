import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import CustomerAwardWorkspace, { type CustomerAwardQuote } from './CustomerAwardWorkspace';

const getByQuote = vi.fn();
const createPurchaseOrder = vi.fn();
const createAward = vi.fn();
const confirmAward = vi.fn();
const convertToOrder = vi.fn();
const proposeQuoteLineMatches = vi.fn();
const getPolicy = vi.fn();
const listUnits = vi.fn();

vi.mock('../../../../api/services/customerAwardService', () => ({
  __esModule: true,
  createCustomerAwardCommandIdentity: () => ({ idempotencyKey: 'key', correlationId: 'corr' }),
  default: {
    getByQuote: (...args: unknown[]) => getByQuote(...args),
    createPurchaseOrder: (...args: unknown[]) => createPurchaseOrder(...args),
    createAward: (...args: unknown[]) => createAward(...args),
    confirmAward: (...args: unknown[]) => confirmAward(...args),
    convertToOrder: (...args: unknown[]) => convertToOrder(...args),
    proposeQuoteLineMatches: (...args: unknown[]) => proposeQuoteLineMatches(...args),
  },
}));

vi.mock('../../../../api/services/commercialPolicyService', () => ({
  __esModule: true,
  default: { getPolicy: (...args: unknown[]) => getPolicy(...args) },
}));

vi.mock('../../../../api/services/uomService', () => ({
  __esModule: true,
  default: { listForTenant: (...args: unknown[]) => listUnits(...args) },
}));

const QUOTED_QUANTITY = 10;
const QUOTED_UNIT_PRICE = 100;

const quote: CustomerAwardQuote = {
  id: 55,
  quoteNo: 'QT-55',
  version: 1,
  commercialCaseId: 7,
  customerId: 9,
  currencyId: 3,
  lines: [{
    id: 501,
    productId: 88,
    productName: 'Ball valve 2in',
    description: 'Ball valve 2in',
    quantity: QUOTED_QUANTITY,
    uomId: 4,
    uomCode: 'EA',
    unitPrice: QUOTED_UNIT_PRICE,
  }],
};

const projection = {
  quoteId: quote.id,
  quoteNo: quote.quoteNo,
  quoteVersion: 1,
  outcome: 'UNAWARDED' as const,
  quotedQuantity: QUOTED_QUANTITY,
  confirmedAwardQuantity: 0,
  remainingQuantity: QUOTED_QUANTITY,
  lines: [{
    quoteItemId: 501,
    productId: 88,
    productName: 'Ball valve 2in',
    description: 'Ball valve 2in',
    quotedQuantity: QUOTED_QUANTITY,
    confirmedAwardQuantity: 0,
    remainingQuantity: QUOTED_QUANTITY,
    uomId: 4,
    uomCode: 'EA',
    unitPrice: QUOTED_UNIT_PRICE,
  }],
  awards: [],
};

const renderWorkspace = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <CustomerAwardWorkspace quote={quote} />
    </QueryClientProvider>,
  );
};

const orderedInput = () => screen.getByLabelText('Quantity the buyer ordered') as HTMLInputElement;
const priceInput = () => screen.getByLabelText('Unit price the buyer ordered at') as HTMLInputElement;
const awardInput = () => screen.getByLabelText('Award quantity') as HTMLInputElement;

const quoteLineSelect = () => screen.getByRole('combobox', { name: /Quote line/ });
const buyerUnitSelect = () => screen.getByRole('combobox', { name: /Buyer unit/ });

const chooseTheQuoteLine = async () => {
  fireEvent.mouseDown(quoteLineSelect());
  fireEvent.click(await screen.findByRole('option', { name: /Ball valve 2in/ }));
};

const chooseTheBuyerUnit = async (name: RegExp) => {
  fireEvent.mouseDown(buyerUnitSelect());
  fireEvent.click(await screen.findByRole('option', { name }));
};

/** The tenant's configured tolerances, as the Commercial Policy screen would have saved them. */
const policy = {
  businessUnitId: 1,
  supplierInputTaxRecoverablePercent: 100,
  outputTaxRatePercent: 15,
  priceTolerancePercent: 2,
  priceToleranceMinimumAmount: 0,
  quantityTolerancePercent: 0,
  version: 2,
  modifiedOn: null,
  modifiedBy: null,
  isDefault: false,
  taxCategories: [],
};

const units = [
  { uomId: 4, businessUnitId: 1, uomCode: 'EA', uomName: 'Each', description: null, isActive: true },
  { uomId: 9, businessUnitId: 1, uomCode: 'BOX', uomName: 'Box', description: null, isActive: true },
];

beforeEach(() => {
  vi.clearAllMocks();
  getByQuote.mockResolvedValue(projection);
  getPolicy.mockResolvedValue(policy);
  listUnits.mockResolvedValue(units);
  createPurchaseOrder.mockResolvedValue({
    id: 900,
    version: 1,
    lines: [{ id: 9001, externalLineReference: '1' }],
  });
  createAward.mockResolvedValue({ id: 700, version: 1 });
  confirmAward.mockResolvedValue({ id: 700, version: 2 });
  convertToOrder.mockResolvedValue({ id: 400, orderNo: 'SO-1' });
});

/**
 * FR-COM-04. The capture form must never pre-fill a buyer field from our own quotation. When it
 * does, the discrepancy engine downstream compares the system against itself and can only ever
 * report agreement, so a real price or quantity difference on a customer's PO would look like the
 * feature never ran.
 */
describe('buyer purchase-order capture', () => {
  it('starts the buyer quantity and price empty rather than seeding them from the quote', async () => {
    renderWorkspace();
    await screen.findByText('Buyer line 1');

    expect(orderedInput().value).toBe('');
    expect(priceInput().value).toBe('');
    expect(orderedInput().value).not.toBe(String(QUOTED_QUANTITY));
    expect(priceInput().value).not.toBe(String(QUOTED_UNIT_PRICE));
  });

  it('keeps the fields empty after a quote line is chosen, and shows the quoted figures beside them', async () => {
    renderWorkspace();
    await screen.findByText('Buyer line 1');

    await chooseTheQuoteLine();

    await screen.findByText(`You quoted ${QUOTED_UNIT_PRICE}`);
    expect(screen.getByText(`You quoted ${QUOTED_QUANTITY}`)).toBeInTheDocument();
    expect(orderedInput().value).toBe('');
    expect(priceInput().value).toBe('');
  });

  it('sends the buyer figures, not the quoted ones, when the award is confirmed', async () => {
    renderWorkspace();
    await screen.findByText('Buyer line 1');
    await chooseTheQuoteLine();

    fireEvent.change(screen.getByLabelText(/Customer PO number/), { target: { value: 'PO-4471' } });
    fireEvent.change(screen.getByLabelText(/Buyer description/), { target: { value: '2in ball valve, flanged' } });
    fireEvent.change(screen.getByLabelText(/Manufacturer part number/), { target: { value: 'E-VLV-2' } });
    fireEvent.change(orderedInput(), { target: { value: '4' } });
    fireEvent.change(priceInput(), { target: { value: '87.25' } });

    fireEvent.click(screen.getByRole('button', { name: /Confirm and create order/ }));

    await waitFor(() => expect(createPurchaseOrder).toHaveBeenCalled());
    const line = createPurchaseOrder.mock.calls[0][0].lines[0];

    expect(line.orderedQuantity).toBe(4);
    expect(line.unitPrice).toBe(87.25);
    expect(line.description).toBe('2in ball valve, flanged');
    expect(line.manufacturerPartNumber).toBe('E-VLV-2');
    // Nothing on the buyer's line may be copied from the quote line it was matched to.
    expect(line.orderedQuantity).not.toBe(QUOTED_QUANTITY);
    expect(line.unitPrice).not.toBe(QUOTED_UNIT_PRICE);
    expect(line.description).not.toBe(quote.lines[0].description);
    expect(line.productId).toBeUndefined();
    // The operator stated no unit, so the PO records none. Explicitly null on the wire — "the
    // buyer's document names no unit" — and never the unit we quoted in.
    expect(line.uomId).toBeNull();
    expect(line.uomId).not.toBe(quote.lines[0].uomId);
  });

  it('warns that the buyer priced the line differently from the quotation', async () => {
    renderWorkspace();
    await screen.findByText('Buyer line 1');
    await chooseTheQuoteLine();

    fireEvent.change(priceInput(), { target: { value: '87.25' } });

    expect(await screen.findByText(/Price differs beyond the 2% tolerance — you quoted 100, buyer ordered 87.25/))
      .toBeInTheDocument();
  });
});

/**
 * FR-COM-04, wiring contract failure #12. A quantity is meaningless without the unit it is measured
 * in, and this screen never sent one — so a buyer PO for "10 boxes" against a quote of "10 each"
 * reached the server as two identical bare numbers and classified as an exact match.
 */
describe('the unit the buyer ordered in', () => {
  it('sends the unit the operator states, so the server has both sides of the comparison', async () => {
    renderWorkspace();
    await screen.findByText('Buyer line 1');
    await chooseTheQuoteLine();
    await chooseTheBuyerUnit(/BOX/);

    fireEvent.change(screen.getByLabelText(/Customer PO number/), { target: { value: 'PO-4471' } });
    fireEvent.change(screen.getByLabelText(/Buyer description/), { target: { value: '2in ball valve' } });
    fireEvent.change(orderedInput(), { target: { value: '10' } });
    fireEvent.change(priceInput(), { target: { value: '100' } });

    fireEvent.click(screen.getByRole('button', { name: /Confirm and create order/ }));

    await waitFor(() => expect(createPurchaseOrder).toHaveBeenCalled());
    expect(createPurchaseOrder.mock.calls[0][0].lines[0].uomId).toBe(9);
  });

  it('warns when the buyer ordered in a unit we did not quote in', async () => {
    renderWorkspace();
    await screen.findByText('Buyer line 1');
    await chooseTheQuoteLine();

    await chooseTheBuyerUnit(/BOX/);

    expect(await screen.findByText(/Unit differs — you quoted in EA, buyer ordered in BOX/)).toBeInTheDocument();
  });

  it('does not warn when the buyer ordered in the unit we quoted in', async () => {
    renderWorkspace();
    await screen.findByText('Buyer line 1');
    await chooseTheQuoteLine();

    await chooseTheBuyerUnit(/EA/);

    await waitFor(() => expect(buyerUnitSelect()).toHaveTextContent(/EA/));
    expect(screen.queryByText(/Unit differs/)).not.toBeInTheDocument();
  });
});

/**
 * FR-COM-04. The capture screen must not contradict the tenant's configured tolerance.
 *
 * It compared against a hardcoded `EPSILON = 0.000001`, so a manager who set 2% on the Commercial
 * Policy screen — honoured by the server and by the review screen — still had their operators told
 * "Price differs" on every rounding difference, on the one screen where acting is still cheap.
 */
describe('the tenant’s configured tolerance', () => {
  it('stays silent on a price difference inside the configured tolerance', async () => {
    renderWorkspace();
    await screen.findByText('Buyer line 1');
    await chooseTheQuoteLine();

    // 101.50 against a quoted 100.00 is 1.5%, inside the 2% the manager configured.
    fireEvent.change(priceInput(), { target: { value: '101.50' } });

    await waitFor(() => expect(priceInput().value).toBe('101.50'));
    expect(screen.queryByText(/Price differs/)).not.toBeInTheDocument();
  });

  it('still reports a price difference outside the configured tolerance', async () => {
    renderWorkspace();
    await screen.findByText('Buyer line 1');
    await chooseTheQuoteLine();

    fireEvent.change(priceInput(), { target: { value: '105' } });

    expect(await screen.findByText(/Price differs beyond the 2% tolerance/)).toBeInTheDocument();
  });

  it('follows the tenant when the tenant turns the tolerance off', async () => {
    getPolicy.mockResolvedValue({ ...policy, priceTolerancePercent: 0 });
    renderWorkspace();
    await screen.findByText('Buyer line 1');
    await chooseTheQuoteLine();

    // The identical purchase order that was clean at 2%. Hardcode any tolerance here and this fails.
    fireEvent.change(priceInput(), { target: { value: '101.50' } });

    expect(await screen.findByText(/Price differs beyond the 0% tolerance/)).toBeInTheDocument();
  });

  /**
   * The quantity chip asked a different question from the server's. It compared the buyer's ordered
   * quantity against the quotation's REMAINING quantity — an ordinary partial award — while the
   * server asks whether what WE are awarding differs from what the buyer ordered.
   */
  it('reports a quantity difference between what the buyer ordered and what we are awarding', async () => {
    renderWorkspace();
    await screen.findByText('Buyer line 1');
    await chooseTheQuoteLine();

    fireEvent.change(orderedInput(), { target: { value: '6' } });
    fireEvent.change(awardInput(), { target: { value: '4' } });

    expect(await screen.findByText(/Quantity differs — buyer ordered 6, awarding 4/)).toBeInTheDocument();
  });

  it('stays silent when the buyer orders less than the quote has left but we award all of it', async () => {
    renderWorkspace();
    await screen.findByText('Buyer line 1');
    await chooseTheQuoteLine();

    // 6 of the 10 still open on the quotation, all of which we accept. A partial award, not a
    // disagreement — the old chip fired on exactly this.
    fireEvent.change(orderedInput(), { target: { value: '6' } });

    await waitFor(() => expect(awardInput().value).toBe('6'));
    expect(screen.queryByText(/Quantity differs/)).not.toBeInTheDocument();
  });
});

/**
 * FR-COM-02. Matching proposes; it never commits. A proposal is only applied to the row when the
 * operator accepts it.
 */
describe('three-key quote line matching', () => {
  it('proposes a quote line from the buyer keys and applies it only once accepted', async () => {
    proposeQuoteLineMatches.mockResolvedValue({
      quoteId: quote.id,
      quoteNo: quote.quoteNo,
      customerId: quote.customerId,
      proposedCount: 1,
      reviewCount: 0,
      lines: [{
        externalLineReference: '1',
        status: 'PROPOSED',
        proposedQuoteItemId: 501,
        matchedKey: 'MANUFACTURER_PART_NUMBER',
        confidence: 'EXACT',
        reason: 'Manufacturer part number E-VLV-2 is the part number quoted on this line.',
        candidates: [{
          quoteItemId: 501,
          quoteDescription: 'Ball valve 2in',
          quotedQuantity: QUOTED_QUANTITY,
          remainingQuantity: QUOTED_QUANTITY,
          quotedUnitPrice: QUOTED_UNIT_PRICE,
          matchedKey: 'MANUFACTURER_PART_NUMBER',
          confidence: 'EXACT',
          reason: 'Manufacturer part number E-VLV-2 is the part number quoted on this line.',
        }],
      }],
    });

    renderWorkspace();
    await screen.findByText('Buyer line 1');
    fireEvent.change(screen.getByLabelText(/Manufacturer part number/), { target: { value: 'E-VLV-2' } });

    fireEvent.click(screen.getByRole('button', { name: /Match to quote lines/ }));

    expect(await screen.findByText(/Matched on manufacturer part number/)).toBeInTheDocument();
    expect(proposeQuoteLineMatches).toHaveBeenCalledWith(expect.objectContaining({
      quoteId: quote.id,
      customerId: quote.customerId,
    }));
    // Proposed, not committed: the row still has no quote line until the operator accepts it.
    expect(quoteLineSelect()).not.toHaveTextContent(/Ball valve 2in/);

    fireEvent.click(await screen.findByRole('button', { name: /Accept 1 proposed match/ }));

    await waitFor(() => expect(quoteLineSelect()).toHaveTextContent(/Ball valve 2in/));
  });
});
