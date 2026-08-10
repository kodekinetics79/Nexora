import axiosInstance from '../axiosInstance';

export type CustomerPurchaseOrderStatus =
  | 'DRAFT'
  | 'CONFIRMED'
  | 'PARTIALLY_AWARDED'
  | 'FULLY_AWARDED'
  | 'CLOSED'
  | 'CANCELLED';

export type CustomerAwardStatus = 'DRAFT' | 'CONFIRMED' | 'ORDERED' | 'CANCELLED';
export type QuoteAwardOutcome = 'UNAWARDED' | 'PARTIALLY_AWARDED' | 'AWARDED';

export interface CustomerPurchaseOrderLine {
  id: number;
  customerPurchaseOrderId: number;
  externalLineReference: string;
  productId?: number | null;
  description: string;
  orderedQuantity: number;
  uomId?: number | null;
  unitPrice?: number | null;
  lineAmount?: number | null;
  version: number;
  /** FR-COM-02 identity keys, as the buyer printed them. Never copied from our quotation. */
  customerItemCode?: string | null;
  manufacturerName?: string | null;
  manufacturerPartNumber?: string | null;
}

export interface CustomerPurchaseOrder {
  id: number;
  commercialCaseId: number;
  customerId: number;
  currencyId: number;
  internalNumber: string;
  externalPoNumber: string;
  poDate: string;
  receivedOn: string;
  status: CustomerPurchaseOrderStatus;
  version: number;
  lines: CustomerPurchaseOrderLine[];
}

export interface CustomerAwardLineAllocation {
  id: number;
  customerAwardId: number;
  customerPurchaseOrderLineId: number;
  quoteItemId: number;
  awardedQuantity: number;
  unitPriceSnapshot: number;
  discountSnapshot: number;
  taxSnapshot: number;
  totalSnapshot: number;
  version: number;
}

export interface CustomerAward {
  id: number;
  awardNumber: string;
  customerPurchaseOrderId: number;
  quoteId: number;
  commercialCaseId: number;
  customerId: number;
  currencyId: number;
  status: CustomerAwardStatus;
  version: number;
  confirmedOn?: string | null;
  allocations: CustomerAwardLineAllocation[];
}

export interface CustomerAwardOrder {
  id: number;
  orderNo: string;
  customerAwardId: number;
  status: string;
  version?: number;
}

export interface ClientPurchaseOrderInboxRow {
  id: number;
  internalNumber: string;
  externalPoNumber: string;
  customerName: string;
  nexoraSerial: string;
  receivedOn: string;
  status: string;
  quoteId?: number | null;
  quoteNumber?: string | null;
  matchOutcome: string;
  discrepancyCount: number;
  customerOrderId?: number | null;
  customerOrderNumber?: string | null;
}

export interface ClientPurchaseOrderMatchLine {
  customerPurchaseOrderLineId: number;
  externalLineReference: string;
  purchaseOrderDescription: string;
  orderedQuantity: number;
  purchaseOrderUnitPrice?: number | null;
  quoteItemId?: number | null;
  quoteDescription?: string | null;
  quotedQuantity?: number | null;
  quotedUnitPrice?: number | null;
  acceptedQuantity?: number | null;
  matchStatus: string;
  differences: string[];
  /**
   * FR-COM-04. The unit each side of the comparison is measured in. Null means that document
   * stated no unit — a visible gap on the screen, never a blank the reader fills in from the
   * other column. A quantity shown without its unit is how "10 boxes" and "10 each" came to read
   * as an exact match.
   */
  purchaseOrderUomId?: number | null;
  purchaseOrderUomCode?: string | null;
  quotedUomId?: number | null;
  quotedUomCode?: string | null;
  customerItemCode?: string | null;
  manufacturerName?: string | null;
  manufacturerPartNumber?: string | null;
}

export type QuoteLineMatchStatus = 'PROPOSED' | 'AMBIGUOUS' | 'REVIEW_REQUIRED';
export type QuoteLineMatchConfidence = 'EXACT' | 'HIGH' | 'MEDIUM' | 'LOW' | 'NONE';

export interface QuoteLineMatchCandidate {
  quoteItemId: number;
  quoteDescription: string;
  quotedQuantity: number;
  remainingQuantity: number;
  quotedUnitPrice: number;
  matchedKey?: string | null;
  confidence: QuoteLineMatchConfidence;
  reason: string;
}

export interface PurchaseOrderLineMatchProposal {
  customerPurchaseOrderLineId?: number | null;
  externalLineReference: string;
  status: QuoteLineMatchStatus;
  proposedQuoteItemId?: number | null;
  matchedKey?: string | null;
  confidence: QuoteLineMatchConfidence;
  reason: string;
  candidates: QuoteLineMatchCandidate[];
}

export interface QuoteLineMatchProposal {
  quoteId: number;
  quoteNo: string;
  customerId?: number | null;
  proposedCount: number;
  reviewCount: number;
  lines: PurchaseOrderLineMatchProposal[];
}

export interface ProposeQuoteLineMatchCommand {
  quoteId: number;
  customerId: number;
  lines: Array<{
    externalLineReference: string;
    description?: string | null;
    customerItemCode?: string | null;
    manufacturerName?: string | null;
    manufacturerPartNumber?: string | null;
    customerPurchaseOrderLineId?: number | null;
  }>;
}

export interface ClientPurchaseOrderMatch {
  header: ClientPurchaseOrderInboxRow;
  customerId: number;
  currencyId: number;
  currencyCode: string;
  poDate: string;
  /** The PO's expected version for the cancel command. */
  version: number;
  awardId?: number | null;
  awardNumber?: string | null;
  awardStatus?: string | null;
  lines: ClientPurchaseOrderMatchLine[];
  /** The award's expected version for the accept-differences and convert commands. */
  awardVersion?: number | null;
  /**
   * FR-COM-04. `"{lineId}:{CODE}"` pairs that currently REFUSE order conversion. Not decoration:
   * this is the same set the server recomputes in convert-to-order, shown here so the reviewer
   * meets the gate before pressing the button instead of as a 409 afterwards.
   */
  blockingDifferences?: string[] | null;
  /** What a named person has already taken responsibility for, with a reason, in the audit ledger. */
  acceptedDifferences?: string[] | null;
  /** Why this purchase order was withdrawn. Null unless the status is CANCELLED. */
  cancellationReason?: string | null;
}

export interface QuoteAwardBalanceLine {
  quoteItemId: number;
  productId?: number | null;
  productName?: string | null;
  description: string;
  quotedQuantity: number;
  confirmedAwardQuantity: number;
  remainingQuantity: number;
  uomId?: number | null;
  uomCode?: string | null;
  unitPrice: number;
}

export interface QuoteAwardProjection {
  quoteId: number;
  quoteNo: string;
  quoteVersion: number;
  outcome: QuoteAwardOutcome;
  quotedQuantity: number;
  confirmedAwardQuantity: number;
  remainingQuantity: number;
  lines: QuoteAwardBalanceLine[];
  awards: CustomerAward[];
}

export interface CreateCustomerPurchaseOrderCommand {
  quoteId: number;
  commercialCaseId: number;
  customerId: number;
  currencyId: number;
  externalPoNumber: string;
  poDate: string;
  receivedOn: string;
  expectedVersion: 0;
  lines: Array<{
    externalLineReference: string;
    productId?: number | null;
    description: string;
    orderedQuantity: number;
    uomId?: number | null;
    unitPrice?: number | null;
    lineAmount?: number | null;
    customerItemCode?: string | null;
    manufacturerName?: string | null;
    manufacturerPartNumber?: string | null;
  }>;
}

/**
 * FR-COM-01. One line as the BUYER stated it in their uploaded purchase order.
 *
 * Every value here came out of the customer's document. Nothing on this shape may ever be
 * defaulted from our own quotation — that is what made the discrepancy check compare the system
 * against itself. A value the document did not state, or stated unreadably, arrives as null with
 * a reason in `reviewReasons`; it is never a substituted number.
 */
export interface CustomerPurchaseOrderDocumentLine {
  lineNumber: number;
  externalLineReference: string;
  description?: string | null;
  orderedQuantity?: number | null;
  quantityText?: string | null;
  unitOfMeasure?: string | null;
  unitPrice?: number | null;
  unitPriceText?: string | null;
  lineAmount?: number | null;
  customerItemCode?: string | null;
  manufacturerName?: string | null;
  manufacturerPartNumber?: string | null;
  sourceAddress: string;
  reviewReasons: string[];
  requiresReview: boolean;
}

export interface CustomerPurchaseOrderDocumentExtraction {
  sourceAttachmentId: number;
  fileName: string;
  contentSha256: string;
  byteSize: number;
  processingPath: string;
  ocrStatus: string;
  externalPoNumber?: string | null;
  poDate?: string | null;
  poDateText?: string | null;
  poDateIsDayMonthAmbiguous: boolean;
  lines: CustomerPurchaseOrderDocumentLine[];
  reviewReasons: string[];
  requiresReview: boolean;
}

export interface CreateCustomerAwardCommand {
  customerPurchaseOrderId: number;
  quoteId: number;
  expectedVersion: 0;
  customerPurchaseOrderExpectedVersion: number;
  quoteExpectedVersion: number;
  allocations: Array<{
    customerPurchaseOrderLineId: number;
    quoteItemId: number;
    awardedQuantity: number;
  }>;
}

export interface VersionedCustomerAwardCommand {
  expectedVersion: number;
}

export interface CancelCustomerAwardCommand extends VersionedCustomerAwardCommand {
  reason: string;
}

/** FR-COM-02. Withdrawing a captured customer PO. The reason is stored, not merely logged. */
export interface CancelCustomerPurchaseOrderCommand {
  expectedVersion: number;
  reason: string;
}

/**
 * FR-COM-04. A named person taking responsibility for one award's price, part or unit differences.
 *
 * A command on the PURCHASE ORDER — `expectedVersion` is the purchase order's — because that is the
 * document the reviewer is deciding about, and because a confirmed award is immutable in the
 * database apart from becoming ORDERED or CANCELLED.
 */
export interface AcceptCustomerPoDifferencesCommand {
  expectedVersion: number;
  customerAwardId: number;
  reason: string;
}

export interface CustomerPoDifferenceAcceptance {
  customerPurchaseOrderId: number;
  customerAwardId: number;
  version: number;
  /** `"{awardId}:{lineId}:{CODE}"` — bound to the award and the buyer line it was given for. */
  acceptedDifferences: string[];
  reason: string;
  acceptedBy: string;
  acceptedOn: string;
}

export interface CommandIdentity {
  idempotencyKey: string;
  correlationId: string;
}

const commandConfig = ({ idempotencyKey, correlationId }: CommandIdentity) => ({
  headers: {
    'Idempotency-Key': idempotencyKey,
    'X-Correlation-ID': correlationId,
  },
});

const randomId = (): string => {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
};

/** Create once per logical command and retain it when retrying that command. */
export const createCustomerAwardCommandIdentity = (command: string): CommandIdentity => {
  const operationId = randomId();
  return {
    idempotencyKey: `customer-award-${command}-${operationId}`,
    correlationId: operationId,
  };
};

const unwrap = <T>(response: { data: T }): T => response.data;

const customerAwardService = {
  searchPurchaseOrders: async (search = "", limit = 100): Promise<ClientPurchaseOrderInboxRow[]> =>
    unwrap(await axiosInstance.get<ClientPurchaseOrderInboxRow[]>("/api/customer-awards/purchase-orders", {
      params: { search: search.trim() || undefined, limit },
    })),
  getPurchaseOrderMatch: async (id: number): Promise<ClientPurchaseOrderMatch> =>
    unwrap(await axiosInstance.get<ClientPurchaseOrderMatch>(`/api/customer-awards/purchase-orders/${id}`)),
  getByQuote: async (quoteId: number): Promise<QuoteAwardProjection> =>
    (await axiosInstance.get<QuoteAwardProjection>(`/api/customer-awards/quote/${quoteId}`)).data,

  /**
   * FR-COM-02. Asks the server to propose a quote line for each buyer line from item code,
   * manufacturer and part number. Read-only: it commits nothing, so it carries no idempotency key
   * and every proposal still has to be confirmed by the operator.
   */
  proposeQuoteLineMatches: async (command: ProposeQuoteLineMatchCommand): Promise<QuoteLineMatchProposal> =>
    unwrap(await axiosInstance.post<QuoteLineMatchProposal>('/api/customer-awards/quote-line-matches', command)),

  proposePurchaseOrderQuoteLineMatches: async (
    purchaseOrderId: number,
    quoteId?: number,
  ): Promise<QuoteLineMatchProposal> =>
    unwrap(await axiosInstance.get<QuoteLineMatchProposal>(
      `/api/customer-awards/purchase-orders/${purchaseOrderId}/quote-line-matches`,
      { params: { quoteId } },
    )),

  createPurchaseOrder: async (
    command: CreateCustomerPurchaseOrderCommand,
    identity: CommandIdentity,
    sourceAttachmentId?: number | null,
  ): Promise<CustomerPurchaseOrder> =>
    // FR-COM-01. When the PO was read from an uploaded document the commercial record must point
    // back at that document, so a reviewer resolving a price discrepancy has the buyer's own file
    // to check the figure against. Same command, one extra evidence link.
    (sourceAttachmentId
      ? (await axiosInstance.post<CustomerPurchaseOrder>(
        '/api/customer-awards/purchase-orders/from-document',
        { sourceAttachmentId, purchaseOrder: command },
        commandConfig(identity),
      )).data
      : (await axiosInstance.post<CustomerPurchaseOrder>(
        '/api/customer-awards/purchase-orders',
        command,
        commandConfig(identity),
      )).data),

  /**
   * Uploads a customer PO (native or scanned PDF, Word, Excel, CSV) through the governed intake
   * door and returns what the document says, for a human to confirm. Nothing is committed here.
   */
  extractPurchaseOrderDocument: async (
    file: File,
  ): Promise<CustomerPurchaseOrderDocumentExtraction> => {
    const form = new FormData();
    form.append('file', file);
    return (await axiosInstance.post<CustomerPurchaseOrderDocumentExtraction>(
      '/api/customer-awards/purchase-orders/document-extractions',
      form,
      { headers: { 'Content-Type': 'multipart/form-data' } },
    )).data;
  },

  /**
   * FR-COM-02. Withdraws a captured customer PO. Refused by the server while any award on it is
   * still live, because an award is a commitment to the customer with its own reason and its own
   * quantity ledger, and withdrawing the paperwork must not silently release it.
   */
  cancelPurchaseOrder: async (
    purchaseOrderId: number,
    command: CancelCustomerPurchaseOrderCommand,
    identity: CommandIdentity,
  ): Promise<CustomerPurchaseOrder> =>
    (await axiosInstance.post<CustomerPurchaseOrder>(
      `/api/customer-awards/purchase-orders/${purchaseOrderId}/cancel`,
      command,
      commandConfig(identity),
    )).data,

  /**
   * FR-COM-04. Records that a named person accepts this award's price, part or unit differences
   * against the quotation. It is the only way past the gate in convert-to-order, and it costs a
   * reason that lands in the governance ledger.
   */
  acceptPurchaseOrderDifferences: async (
    purchaseOrderId: number,
    command: AcceptCustomerPoDifferencesCommand,
    identity: CommandIdentity,
  ): Promise<CustomerPoDifferenceAcceptance> =>
    (await axiosInstance.post<CustomerPoDifferenceAcceptance>(
      `/api/customer-awards/purchase-orders/${purchaseOrderId}/accept-differences`,
      command,
      commandConfig(identity),
    )).data,

  createAward: async (
    command: CreateCustomerAwardCommand,
    identity: CommandIdentity,
  ): Promise<CustomerAward> =>
    (await axiosInstance.post<CustomerAward>(
      '/api/customer-awards',
      command,
      commandConfig(identity),
    )).data,

  confirmAward: async (
    awardId: number,
    command: VersionedCustomerAwardCommand,
    identity: CommandIdentity,
  ): Promise<CustomerAward> =>
    (await axiosInstance.post<CustomerAward>(
      `/api/customer-awards/${awardId}/confirm`,
      command,
      commandConfig(identity),
    )).data,

  cancelAward: async (
    awardId: number,
    command: CancelCustomerAwardCommand,
    identity: CommandIdentity,
  ): Promise<CustomerAward> =>
    (await axiosInstance.post<CustomerAward>(
      `/api/customer-awards/${awardId}/cancel`,
      command,
      commandConfig(identity),
    )).data,

  convertToOrder: async (
    awardId: number,
    command: VersionedCustomerAwardCommand,
    identity: CommandIdentity,
  ): Promise<CustomerAwardOrder> =>
    (await axiosInstance.post<CustomerAwardOrder>(
      `/api/customer-awards/${awardId}/convert-to-order`,
      command,
      commandConfig(identity),
    )).data,
};

export default customerAwardService;
