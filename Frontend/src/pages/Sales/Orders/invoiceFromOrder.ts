import type { InvoiceLineCommand } from '../../../api/services/commercialFinanceService';
import type { DeliveredQuantityLine } from '../../../api/services/deliveryService';
import type { OrderItemDTO } from '../../../api/services/orderService';
import type { ReceivableDocument } from '../../../api/services/commercialFinanceService';

/**
 * Gate 7 / FR-DLM-02, commercial half — the arithmetic and the wording behind the line-level
 * invoice screen. Kept out of the component so both are testable without a DOM, and so there is one
 * place to read what the ceiling on this screen actually is.
 *
 * THE DEFECT THIS EXISTS TO CLOSE. The only invoice call in the product posted `lines: null`, and
 * the server expands a null line set to the FULL ORDERED quantity. Every short delivery — a
 * customer signing for 7 of 10 — therefore produced a guaranteed 409 against the accepted-quantity
 * ceiling, with no screen anywhere that could ask for 7. A control nobody can obey is a control
 * somebody eventually routes around.
 *
 * The server ceiling is NOT weakened here and is not re-implemented here. It is mirrored, so the
 * operator can see the number before they hit it:
 * `alreadyInvoiced + requested <= acceptedQuantity`, evaluated at draft
 * (`CommercialFinanceApplicationService.CreateInvoiceAsync`) and again at issue. Everything below
 * is presentation of that same inequality; the server remains the only authority, and every
 * refusal it makes is surfaced in its own words.
 */

/** How a line's ceiling came to be what it is. Never a blank — see {@link CAP_REASON_LABEL}. */
export type InvoiceCapReason =
  /** Units have been accepted and not yet billed. The normal case. */
  | 'AVAILABLE'
  /** Nothing has left the warehouse against this line yet. */
  | 'NOT_DESPATCHED'
  /** Goods are out, and no proof of delivery says the customer took any of them. */
  | 'AWAITING_ACCEPTANCE'
  /** Everything the customer accepted is already on an issued invoice. */
  | 'FULLY_INVOICED';

export const CAP_REASON_LABEL: Record<InvoiceCapReason, string> = {
  AVAILABLE: '',
  NOT_DESPATCHED: 'Nothing despatched yet',
  AWAITING_ACCEPTANCE: 'Nothing accepted yet',
  FULLY_INVOICED: 'Already invoiced in full',
};

/**
 * One row of the screen. Every quantity on it is a server figure; the only derived number is
 * {@link cap}, and it is derived by the same subtraction the server performs.
 */
export interface InvoiceLineDraft {
  orderItemId: number;
  productName: string;
  unitPrice: number;
  orderedQuantity: number;
  despatchedQuantity: number;
  awaitingConfirmationQuantity: number;
  acceptedQuantity: number;
  refusedQuantity: number;
  /**
   * Units on ISSUED invoices for this order line. `null` means the already-invoiced position could
   * not be read — a NAMED gap, rendered as one, never silently treated as zero. When it is null the
   * cap falls back to the accepted quantity, which is the more generous of the two, so the screen
   * can offer a quantity the server then refuses with its own explanation rather than quietly
   * under-billing.
   */
  alreadyInvoicedQuantity: number | null;
  /** The ceiling this row may not exceed, and the number the screen shows the operator. */
  cap: number;
  capReason: InvoiceCapReason;
}

/**
 * Builds the rows from three server reads: the delivered-quantity ledger
 * (`GET /api/delivery/orders/{id}/delivered-quantities`), the order lines, and the issued
 * receivable documents.
 *
 * **The LEDGER decides which rows exist, not the order response.** `DeliveredQuantityLedger`
 * returns every awarded line whose `IsActive` is true — including lines nothing has shipped
 * against, which come back as zeroes — while `GET /api/Order/{id}` maps `order.OrderItems`
 * unfiltered and so still carries withdrawn lines. Driving the screen from the order response
 * would put a line on an invoice screen that the server's own ceiling has no row for. The order
 * response is used for one thing only: the product name and unit price behind an id, because the
 * ledger carries neither.
 *
 * A ledger line with no matching order item is still shown, named by its id. That is a visible
 * gap, not a hidden row — the alternative is a screen that quietly bills less than the order has.
 *
 * @param alreadyInvoiced `null` when the issued-document read failed or was skipped. Distinct from
 *   an empty map, which means "read successfully, nothing invoiced".
 */
export const buildInvoiceLineDrafts = (
  delivered: readonly DeliveredQuantityLine[],
  items: readonly OrderItemDTO[],
  alreadyInvoiced: ReadonlyMap<number, number> | null,
): InvoiceLineDraft[] => delivered.map((ledger) => {
  const item = items.find((candidate) => candidate.id === ledger.orderItemId);
  const invoicedQuantity = alreadyInvoiced === null
    ? null
    : alreadyInvoiced.get(ledger.orderItemId) ?? 0;

  // The server's inequality, rearranged: requested <= accepted - alreadyInvoiced.
  const rawCap = ledger.acceptedQuantity - (invoicedQuantity ?? 0);
  const cap = rawCap > 0 ? rawCap : 0;

  let capReason: InvoiceCapReason = 'AVAILABLE';
  if (cap <= 0) {
    if (ledger.acceptedQuantity > 0) capReason = 'FULLY_INVOICED';
    else if (ledger.despatchedQuantity > 0) capReason = 'AWAITING_ACCEPTANCE';
    else capReason = 'NOT_DESPATCHED';
  }

  return {
    orderItemId: ledger.orderItemId,
    productName: item?.productName || item?.description || `Order line ${ledger.orderItemId}`,
    unitPrice: item?.unitPrice ?? 0,
    // Awarded comes from the ledger, which is the same figure the ordered-quantity ceiling uses.
    orderedQuantity: ledger.awardedQuantity,
    despatchedQuantity: ledger.despatchedQuantity,
    awaitingConfirmationQuantity: ledger.awaitingConfirmationQuantity,
    acceptedQuantity: ledger.acceptedQuantity,
    refusedQuantity: ledger.refusedQuantity,
    alreadyInvoicedQuantity: invoicedQuantity,
    cap,
    capReason,
  };
});

/**
 * Units already billed per order line, counted exactly the way
 * `CommercialFinanceApplicationService` counts them: DocumentType `Invoice`, Status `Issued`, this
 * order. Drafts do not count — the server does not count them either — and credit notes do not net
 * off, because they do not net off on the server's side of the ceiling either. A client sum that
 * disagreed with the server's would be worse than no sum at all.
 */
export const sumIssuedInvoicedQuantities = (
  documents: readonly ReceivableDocument[],
  orderId: number,
): Map<number, number> => {
  const totals = new Map<number, number>();
  for (const document of documents) {
    if (document.orderId !== orderId) continue;
    if (document.documentType !== 'Invoice') continue;
    if (document.status !== 'Issued') continue;
    for (const line of document.lines ?? []) {
      if (line.orderItemId === undefined || line.orderItemId === null) continue;
      totals.set(line.orderItemId, (totals.get(line.orderItemId) ?? 0) + line.quantity);
    }
  }
  return totals;
};

/** The lines that will actually be posted: positive quantities only, in a stable order. */
export const toInvoiceLineCommands = (
  drafts: readonly InvoiceLineDraft[],
  quantities: ReadonlyMap<number, number>,
): InvoiceLineCommand[] => drafts
  .map((draft) => ({ orderItemId: draft.orderItemId, quantity: quantities.get(draft.orderItemId) ?? 0 }))
  .filter((line) => line.quantity > 0)
  .sort((a, b) => a.orderItemId - b.orderItemId);

/** Rows the operator has asked for more of than the ceiling allows. Never silently clamped. */
export const linesOverCap = (
  drafts: readonly InvoiceLineDraft[],
  quantities: ReadonlyMap<number, number>,
): InvoiceLineDraft[] => drafts.filter((draft) => (quantities.get(draft.orderItemId) ?? 0) > draft.cap);

/**
 * FNV-1a, run twice with different offset bases, to 16 hex characters.
 *
 * Not a cryptographic digest and not pretending to be one: this is a content discriminator for an
 * idempotency key, and the only property required of it is that two different line sets are
 * overwhelmingly unlikely to collide within one order. `crypto.subtle` is the stronger tool and is
 * rejected on purpose — it is async, and an idempotency key that arrives a tick after the click has
 * to be threaded through the mutation, which is exactly the kind of seam a retry falls through.
 */
const digest = (value: string): string => {
  const fnv = (offset: number): number => {
    let hash = offset;
    for (let index = 0; index < value.length; index += 1) {
      hash ^= value.charCodeAt(index);
      // 32-bit FNV prime (16777619) by shift-and-add, kept in Math.imul to stay integral.
      hash = Math.imul(hash, 16777619) >>> 0;
    }
    return hash >>> 0;
  };
  return (fnv(0x811c9dc5).toString(16).padStart(8, '0')
    + fnv(0x01000193).toString(16).padStart(8, '0'));
};

/** Canonical text for a line set: sorted, so row order on screen cannot change the key. */
export const canonicaliseLines = (lines: readonly InvoiceLineCommand[]): string => [...lines]
  .sort((a, b) => a.orderItemId - b.orderItemId)
  .map((line) => `${line.orderItemId}:${line.quantity}`)
  .join('|');

/**
 * THE IDEMPOTENCY KEY, and why it is shaped this way.
 *
 * It was `order-invoice-${orderId}-full`: one constant string per order, forever. That is wrong in
 * both directions once quantities become editable.
 *
 *  - The server stores the key with a hash of the request body. A SECOND, genuinely different
 *    invoice run for the same order — a re-supply of the three cartons the customer refused last
 *    month — reuses the constant key, the hashes differ, and the operator is told "the idempotency
 *    key was already used with a different request" for a perfectly legitimate invoice they can
 *    never raise.
 *  - Worse, when the corrected quantities happen to MATCH the first attempt's, the server replays
 *    and returns the earlier document. The screen reports success and nothing was created. That is
 *    wiring-contract failure #7 — a control that reports success while doing nothing — and it is
 *    silent, which is why it is the failure worth engineering against here.
 *
 * The key therefore carries two terms:
 *
 *  1. `sessionNonce` — one random value per opening of the dialog. It is what makes a deliberate
 *     second invoice run a NEW request rather than a replay of the first, even when the operator
 *     types identical quantities. Progressive billing is a normal thing to do, and it must not
 *     depend on the numbers happening to differ.
 *  2. `quantityDigest` — a digest of the (orderItemId, quantity) pairs actually being posted. This
 *     is the term the brief requires and it is what stops the other failure: the operator submits
 *     10, the server refuses with the accepted-quantity ceiling, they correct it to 7 and press the
 *     button again WITHOUT closing the dialog. Same nonce, different quantities, different key —
 *     so the corrected attempt is evaluated on its merits instead of being swallowed as a replay.
 *
 * What the pair still buys us, and what it costs. A true retry of the SAME submission — a double
 * click, or an axios retry after a dropped response — reproduces both terms exactly, so the server
 * replays and no second draft appears. The residual case is a lost response followed by the
 * operator closing and reopening the dialog: a new nonce, so a duplicate DRAFT. That is the
 * direction the failure is deliberately pointed, because a duplicate draft is visible, cancellable
 * and cannot bill anyone — the issue-time ceiling counts only ISSUED invoices, so at most one of
 * the two can ever be issued — whereas a silently swallowed invoice is money nobody notices was
 * never asked for.
 *
 * Length: `ValidateKey` on the server allows 128 characters. This produces roughly 80.
 */
export const invoiceIdempotencyKey = (
  orderId: number,
  sessionNonce: string,
  lines: readonly InvoiceLineCommand[],
): string => `order-invoice-${orderId}-${sessionNonce}-${digest(canonicaliseLines(lines))}`;

/** One per dialog opening. See {@link invoiceIdempotencyKey} for what this term is for. */
export const newInvoiceSessionNonce = (): string =>
  globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;

export interface InvoiceConflictExplanation {
  /**
   * The server's sentence, verbatim. The client never rewrites it and never contradicts it — see
   * the wiring contract's interface checklist.
   */
  serverDetail: string;
  /** The order line the server named, when this screen holds it. `null` when it named none. */
  orderItemId: number | null;
  /**
   * The one thing the server could not say: which PRODUCT that line id is, and how the numbers on
   * this screen relate to the ceiling it refused against. `null` when no line was named.
   */
  context: string | null;
}

/**
 * Every 409 the invoice ceiling raises names its order line as "order line {id}" — the draft-time
 * message, the ordered-quantity message and the issue-time message all do. The server cannot name
 * the product, because the receivable module has no product names; this screen does. So the id is
 * lifted out and turned into a sentence a person can act on, and the server's own text is kept
 * underneath it rather than replaced.
 *
 * If the id cannot be matched the server text still shows, unedited. A parse that fails must
 * degrade to "you see exactly what the server said", never to a client-invented explanation.
 */
export const explainInvoiceConflict = (
  serverDetail: string,
  drafts: readonly InvoiceLineDraft[],
  quantities: ReadonlyMap<number, number>,
): InvoiceConflictExplanation => {
  const match = /order line (\d+)/i.exec(serverDetail);
  const orderItemId = match ? Number(match[1]) : null;
  const draft = orderItemId === null
    ? undefined
    : drafts.find((row) => row.orderItemId === orderItemId);
  if (!draft) return { serverDetail, orderItemId, context: null };

  const requested = quantities.get(draft.orderItemId) ?? 0;
  const invoiced = draft.alreadyInvoicedQuantity;
  return {
    serverDetail,
    orderItemId: draft.orderItemId,
    context: `${draft.productName} (order line ${draft.orderItemId}): you asked to invoice `
      + `${requested}, and the customer has accepted ${draft.acceptedQuantity} of the `
      + `${draft.orderedQuantity} ordered (${draft.despatchedQuantity} despatched, `
      + `${draft.refusedQuantity} not accepted`
      + (invoiced === null ? '' : `, ${invoiced} already invoiced`)
      + `). Reduce this line to ${draft.cap} or record the delivery that covers the rest.`,
  };
};
