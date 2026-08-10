import { describe, expect, it } from 'vitest';
import type { ReceivableDocument } from '../../../api/services/commercialFinanceService';
import type { DeliveredQuantityLine } from '../../../api/services/deliveryService';
import type { OrderItemDTO } from '../../../api/services/orderService';
import {
  buildInvoiceLineDrafts,
  canonicaliseLines,
  explainInvoiceConflict,
  invoiceIdempotencyKey,
  sumIssuedInvoicedQuantities,
} from './invoiceFromOrder';

/**
 * The arithmetic and the wording behind the line-level invoice screen, without a DOM.
 *
 * `InvoiceFromOrderDialog.test.tsx` proves the wiring end to end at the HTTP layer; this file
 * pins the rules that wiring carries — chiefly the two the idempotency key has to satisfy at once,
 * which are easy to break by "simplifying" the key and impossible to notice afterwards.
 *
 * All fixture data is obviously synthetic.
 */

const ledgerLine = (over: Partial<DeliveredQuantityLine> & { orderItemId: number }): DeliveredQuantityLine => ({
  awardedQuantity: 10,
  despatchedQuantity: 0,
  acceptedQuantity: 0,
  awaitingConfirmationQuantity: 0,
  refusedQuantity: 0,
  outstandingQuantity: 10,
  isFullyDelivered: false,
  ...over,
});

const orderItem = (over: Partial<OrderItemDTO> & { id: number }): OrderItemDTO => ({
  productId: 1,
  productName: 'Gate valve',
  quantity: 10,
  unitPrice: 100,
  discount: 0,
  taxAmount: 0,
  totalAmount: 1000,
  ...over,
});

describe('the idempotency key', () => {
  const nonce = 'synthetic-nonce-0000';

  it('differs when a quantity differs — the corrected second attempt must not replay the first', () => {
    const first = invoiceIdempotencyKey(900, nonce, [{ orderItemId: 5001, quantity: 10 }]);
    const second = invoiceIdempotencyKey(900, nonce, [{ orderItemId: 5001, quantity: 7 }]);
    expect(first).not.toBe(second);
  });

  it('differs when the LINE SET differs even though every quantity is unchanged', () => {
    const one = invoiceIdempotencyKey(900, nonce, [{ orderItemId: 5001, quantity: 7 }]);
    const two = invoiceIdempotencyKey(900, nonce, [
      { orderItemId: 5001, quantity: 7 }, { orderItemId: 5002, quantity: 7 },
    ]);
    expect(one).not.toBe(two);
  });

  it('is identical for the same request, so a retry replays instead of raising a second draft', () => {
    const lines = [{ orderItemId: 5002, quantity: 3 }, { orderItemId: 5001, quantity: 7 }];
    expect(invoiceIdempotencyKey(900, nonce, lines))
      .toBe(invoiceIdempotencyKey(900, nonce, [...lines].reverse()));
  });

  it('differs across dialog openings, so a deliberate second invoice run is never a replay', () => {
    const lines = [{ orderItemId: 5001, quantity: 7 }];
    expect(invoiceIdempotencyKey(900, 'opening-a', lines))
      .not.toBe(invoiceIdempotencyKey(900, 'opening-b', lines));
  });

  it('fits the 128 characters the server accepts', () => {
    const key = invoiceIdempotencyKey(
      9007199254740991,
      '00000000-0000-4000-8000-000000000000',
      Array.from({ length: 50 }, (_, index) => ({ orderItemId: index, quantity: 12345.6789 })),
    );
    expect(key.length).toBeLessThanOrEqual(128);
  });

  it('canonicalises by order line id, not by the order rows happened to be listed in', () => {
    expect(canonicaliseLines([{ orderItemId: 9, quantity: 1 }, { orderItemId: 2, quantity: 4 }]))
      .toBe('2:4|9:1');
  });
});

describe('what is already invoiced', () => {
  const documents = [
    // Counts: an issued invoice on this order.
    { id: 1, orderId: 900, documentType: 'Invoice', status: 'Issued', lines: [{ orderItemId: 5001, quantity: 4 }] },
    // Does not count: still a draft. The server does not count drafts either.
    { id: 2, orderId: 900, documentType: 'Invoice', status: 'Draft', lines: [{ orderItemId: 5001, quantity: 3 }] },
    // Does not count: a credit note does not net off the server's side of the ceiling.
    { id: 3, orderId: 900, documentType: 'CreditNote', status: 'Issued', lines: [{ orderItemId: 5001, quantity: 2 }] },
    // Does not count: another order for the same customer.
    { id: 4, orderId: 901, documentType: 'Invoice', status: 'Issued', lines: [{ orderItemId: 5001, quantity: 9 }] },
  ] as unknown as ReceivableDocument[];

  it('counts exactly what the server counts', () => {
    expect(sumIssuedInvoicedQuantities(documents, 900).get(5001)).toBe(4);
  });
});

describe('the rows on the screen', () => {
  it('comes from the delivery ledger, so a withdrawn order line cannot appear', () => {
    const drafts = buildInvoiceLineDrafts(
      [ledgerLine({ orderItemId: 5001, despatchedQuantity: 10, acceptedQuantity: 7, refusedQuantity: 3 })],
      [orderItem({ id: 5001 }), orderItem({ id: 5999, productName: 'Withdrawn line' })],
      new Map(),
    );
    expect(drafts.map((draft) => draft.orderItemId)).toEqual([5001]);
    expect(drafts[0].cap).toBe(7);
    expect(drafts[0].capReason).toBe('AVAILABLE');
  });

  it('names a ledger line the order response does not describe rather than hiding it', () => {
    const [draft] = buildInvoiceLineDrafts([ledgerLine({ orderItemId: 7777 })], [], new Map());
    expect(draft.productName).toBe('Order line 7777');
  });

  it('gives a zero ceiling a reason, and the three reasons are different', () => {
    const [nothingShipped, awaiting, billed] = buildInvoiceLineDrafts(
      [
        ledgerLine({ orderItemId: 1 }),
        ledgerLine({ orderItemId: 2, despatchedQuantity: 10, awaitingConfirmationQuantity: 10 }),
        ledgerLine({ orderItemId: 3, despatchedQuantity: 10, acceptedQuantity: 10 }),
      ],
      [],
      new Map([[3, 10]]),
    );
    expect(nothingShipped.capReason).toBe('NOT_DESPATCHED');
    expect(awaiting.capReason).toBe('AWAITING_ACCEPTANCE');
    expect(billed.capReason).toBe('FULLY_INVOICED');
  });

  it('reduces the ceiling by what is already issued, and never goes negative', () => {
    const [draft] = buildInvoiceLineDrafts(
      [ledgerLine({ orderItemId: 5001, despatchedQuantity: 10, acceptedQuantity: 7 })],
      [orderItem({ id: 5001 })],
      new Map([[5001, 9]]),
    );
    expect(draft.cap).toBe(0);
    expect(draft.alreadyInvoicedQuantity).toBe(9);
  });

  it('marks the already-invoiced position as UNKNOWN rather than zero when it could not be read', () => {
    const [draft] = buildInvoiceLineDrafts(
      [ledgerLine({ orderItemId: 5001, despatchedQuantity: 10, acceptedQuantity: 7 })],
      [orderItem({ id: 5001 })],
      null,
    );
    expect(draft.alreadyInvoicedQuantity).toBeNull();
    expect(draft.cap).toBe(7);
  });
});

describe('the 409 explanation', () => {
  const drafts = buildInvoiceLineDrafts(
    [ledgerLine({ orderItemId: 5001, despatchedQuantity: 10, acceptedQuantity: 7, refusedQuantity: 3 })],
    [orderItem({ id: 5001 })],
    new Map(),
  );

  it('keeps the server sentence intact and adds only the product name the server cannot know', () => {
    const detail = 'Invoice quantity exceeds the quantity the customer has accepted for order '
      + 'line 5001: 10 despatched, 7 accepted, 0 already invoiced, 9 requested.';
    const explained = explainInvoiceConflict(detail, drafts, new Map([[5001, 9]]));
    expect(explained.serverDetail).toBe(detail);
    expect(explained.orderItemId).toBe(5001);
    expect(explained.context).toContain('Gate valve (order line 5001)');
    expect(explained.context).toContain('Reduce this line to 7');
  });

  it('adds nothing at all when the server named a line this screen does not hold', () => {
    const explained = explainInvoiceConflict(
      'Issuing this document would exceed order line 4242 quantity.', drafts, new Map());
    expect(explained.context).toBeNull();
    expect(explained.serverDetail).toBe('Issuing this document would exceed order line 4242 quantity.');
  });

  it('degrades to the server text when no line is named', () => {
    const explained = explainInvoiceConflict('Only an active order with lines can be invoiced.', drafts, new Map());
    expect(explained.orderItemId).toBeNull();
    expect(explained.context).toBeNull();
  });
});
