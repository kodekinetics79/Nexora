import axiosInstance from '../axiosInstance';

/**
 * What the server did when asked to remove a quotation. `deleted` is false for the normal case —
 * anything past DRAFT is withdrawn and kept, because the customer holds a document with those
 * numbers on it and the price attestations behind it are evidence, not clutter.
 */
export interface QuoteRemovalResult {
  quoteNo: string;
  mode: 'DRAFT_DISCARDED' | 'WITHDRAWN';
  removedOn: string;
  deleted: boolean;
  message: string;
}

/**
 * One priced line of a quote, mirroring `DTOs/QuoteDTOs/QuoteResponseDTO.cs`.
 *
 * This was `any[]` until the tax fields went missing on one of the two server-side projections and
 * nothing on this side could have noticed. `any` on a wire contract is how a field the server sends
 * ends up read by no screen, and how a field no server sends ends up read by one.
 *
 * Optional means "the server may legitimately omit it", not "we did not check".
 */
export interface QuoteLineDTO {
  id: number;
  quoteId: number;
  rfqItemId?: number | null;
  productId?: number | null;
  productName?: string | null;
  itemDescription?: string | null;
  quantity: number;
  unitOfMeasure?: string | null;
  customerLineRef?: string | null;
  unitPrice: number;
  /** Tax-INCLUSIVE line total (calculation version 2): `taxableBase + taxAmount`. */
  totalAmount: number;
  discount?: number | null;
  discountTypeId?: number | null;
  discountTypeName?: string | null;
  discountValue?: number | null;
  /** Server-derived. Null means the tax was never derived, which is what blocks the send. */
  taxAmount?: number | null;
  taxCategory?: string | null;
  taxCategoryReason?: string | null;
  taxRatePercentApplied?: number | null;
  /**
   * This line's share of the QUOTE-LEVEL discount, as the server allocated and stored it.
   * It cannot be recovered from the other figures — every attempt subtracts a tax-inclusive total
   * from a tax-exclusive net — so a screen showing the header discount has to be told it.
   * Null on lines written before the column existed.
   */
  headerDiscountAllocated?: number | null;
  /** What output tax was charged on: `totalAmount - taxAmount`. The printed line column's figure. */
  taxableBase: number;
  deliveryLeadTime?: number | null;
  // What the customer asked for, read through the linked RFQ line. Null when the quote has no RFQ.
  requestedManufacturerName?: string | null;
  requestedManufacturerPartNumber?: string | null;
  requestedItemMaterialCode?: string | null;
  requestedAlternatePartNumber?: string | null;
  requestedDeliveryDate?: string | null;
  requestedLeadTimeDays?: number | null;
  requestedCurrency?: string | null;
}

export interface QuoteDTO {
  id: number;
  quoteNo: string;
  rfqId?: number;
  rfqNo?: string;
  leadId?: number;
  sourceLeadRevision: number;
  sourceRfqRevision: number;
  revisionImpact?: string | null;
  commercialCaseId?: number;
  commercialCaseReference?: string | null;
  nexoraSerial?: string | null;
  lifecycleVersion: number;
  version: number;
  customerId?: number;
  contactId?: number | null;
  contactName?: string | null;
  customerName?: string;
  businessUnitId: number;
  businessUnitName: string;
  customerEmail?: string;
  quoteDate: string;
  validUntil: string;
  statusId: number;
  statusValue: string;
  currencyId?: number;
  currencyCode?: string;
  totalAmount: number;
  headerRemarks?: string;
  createdBy: string;
  createdDate: string;
  modifiedBy?: string;
  modifiedDate?: string;
  discountTypeId?: number;
  discountTypeName?: string;
  discountValue?: number;
  itemCount: number;
  quoteItems: QuoteLineDTO[];
  // Outcome capture + SLA staleness (WP-A4)
  statusCode?: string;
  sentOn?: string | null;
  respondedOn?: string | null;
  outcomeOn?: string | null;
  outcomeReasonId?: number | null;
  outcomeReasonName?: string | null;
  outcomeNote?: string | null;
  isStale?: boolean;
  daysSinceSent?: number | null;
  // Reasoned validity extensions (R7)
  /** When the validity date was last moved by an explicit, reasoned extend command. */
  validityExtendedOn?: string | null;
  /** Lifecycle permits extending: sent to the customer, no outcome recorded yet. */
  canExtendValidity?: boolean;
}

export interface OutcomeReasonDTO {
  id: number;
  code: string;
  label: string;
}

// ==== Below-floor holds (WP-B3) + revisions-lite (WP-B4) ====

/** Result of a send attempt: either it went out, or it was parked in Approvals. */
export interface QuoteSendOutcome {
  held: boolean;
  /** Plain-language hold info when held ("Quote #…: N line(s) below floor by up to X%"). */
  message?: string;
  approvalId?: string;
  /**
   * R5: nothing was sent because the price source is unconfirmed, or a price changed after
   * it was last confirmed. The rep must confirm again before the quote can go out.
   */
  priceAttestationRequired?: boolean;
  /**
   * R17: nothing was sent because a line's output tax was never calculated — this business unit has
   * no output tax rate configured, the line has not been priced, or a non-standard tax treatment
   * carries no stated reason. A quotation with no VAT shown on it is treated as tax-inclusive, so
   * the difference would come out of the seller's margin.
   */
  taxDerivationRequired?: boolean;
  /**
   * The customer HAS the quote: the delivery worker had already completed this send (the server
   * replayed it). Only this justifies telling the rep "emailed".
   */
  delivered?: boolean;
  /**
   * A delivery row was written and a background worker will send it later. Nothing has reached
   * the customer yet, and the worker can still refuse (no transmitting mailbox, a render failure)
   * — in which case the fixed delivery key makes this quote number permanently unsendable. The
   * screen must not call this "emailed".
   */
  queuedForDelivery?: boolean;
}

/**
 * The words a rep is shown for a send the server accepted. Shared by every screen that sends a
 * quote, so they cannot drift from each other or from the server's own distinction.
 *
 * Until this existed, `sendEmail` discarded the 202 body — `{ queuedForDelivery, delivered }` —
 * and every accepted send was announced as "Quote emailed to the customer". Almost every send is
 * merely QUEUED: the row is handed to `QuoteDeliveryWorker`, which can dead-letter it minutes
 * later with nobody watching. A rep who read "emailed" closed the tab; the customer had nothing.
 */
export const describeQuoteSendOutcome = (
  result: Pick<QuoteSendOutcome, 'delivered' | 'queuedForDelivery'>,
): { delivered: boolean; message: string } =>
  result.delivered
    ? { delivered: true, message: 'Quote emailed to the customer' }
    : {
        delivered: false,
        message: 'Quote queued for delivery. It is not with the customer yet — the status changes to Sent once the email is confirmed.',
      };

// ==== Price-provenance attestation (Decision Register R5) ====

/** Where a quoted price came from. These are the only two the server accepts. */
export type PriceAttestationSource = 'SALES_MANAGER' | 'SUPPLIER_QUOTE';

export interface QuotePriceAttestationLine {
  quoteItemId: number;
  rfqItemId?: number | null;
  itemDescription?: string | null;
  quantity: number;
  unitPrice: number;
}

/** Whether this quote may be sent, and what was last confirmed. */
/** One reason a quote's send would be refused, and the screen that fixes it. */
export interface QuoteSendBlocker {
  /** Stable code for branching. Never render this. */
  code: string;
  /** The sentence the rep reads: what is wrong AND what to do. */
  message: string;
  setupLabel?: string | null;
  setupPath?: string | null;
}

export interface QuoteSendReadiness {
  quoteId: number;
  canSend: boolean;
  blockers: QuoteSendBlocker[];
  /**
   * UNCERTAIN when a previous delivery was interrupted and never confirmed — the customer may
   * already hold this quote, and nothing is resent automatically. NOT_DELIVERED when it
   * definitively failed. Null when no delivery has ended terminally.
   */
  deliveryOutcome?: 'UNCERTAIN' | 'NOT_DELIVERED' | null;
  deliveryInFlight?: boolean;
}

export interface QuotePriceAttestationStatus {
  quoteId: number;
  satisfied: boolean;
  /** Why the send would be refused; null when satisfied. */
  reason?: string | null;
  source?: PriceAttestationSource | null;
  sourceReference?: string | null;
  confirmedBy?: string | null;
  confirmedOn?: string | null;
  /** A confirmation exists but a price changed since, so it no longer covers the quote. */
  supersededByPriceChange: boolean;
  currencyId?: number | null;
  currencyCode?: string | null;
  currentLines: QuotePriceAttestationLine[];
  attestedLines: QuotePriceAttestationLine[];
}

export interface QuoteRevisionInfoDTO {
  quoteId: number;
  quoteNo: string;
  revisionNo: number;
  revisionOfQuoteId?: number | null;
  revisionOfQuoteNo?: string | null;
  supersededByQuoteId?: number | null;
  supersededByQuoteNo?: string | null;
  chainLocked: boolean;
  canRevise: boolean;
}

// ==== Reasoned quote-validity extensions (Decision Register R7) ====

/** One recorded move of a quote's validity date, with the reason that justified it. */
export interface QuoteValidityExtensionDTO {
  id: number;
  quoteId: number;
  previousValidUntil?: string | null;
  newValidUntil: string;
  reason: string;
  extendedBy: string;
  extendedOn: string;
}

export interface QuoteValidityExtensionResult {
  quoteId: number;
  quoteNo: string;
  validUntil?: string | null;
  validityExtendedOn?: string | null;
  /** Unchanged by extending — the commercial offer is the same offer. */
  revisionNo: number;
  replayed: boolean;
  extension?: QuoteValidityExtensionDTO | null;
}

export type QuoteOutcome = 'won' | 'lost' | 'expired';

export interface PaginatedQuotes {
  items: QuoteDTO[];
  totalItems: number;
}

export interface QuoteParams {
  businessUnitId?: number;
  pageNumber?: number;
  pageSize?: number;
  search?: string;
  state?: string;
}

const quoteService = {
  getAll: async (params: QuoteParams = {}): Promise<PaginatedQuotes> => {
    const { data } = await axiosInstance.get('/api/Quote', { params });
    return data;
  },

  getById: async (id: number, businessUnitId?: number): Promise<QuoteDTO> => {
    const params = businessUnitId ? { businessUnitId } : {};
    const { data } = await axiosInstance.get(`/api/Quote/${id}`, { params });
    return data;
  },

  create: async (quoteData: any): Promise<QuoteDTO> => {
    const { data } = await axiosInstance.post('/api/Quote', quoteData);
    return data;
  },

  update: async (id: number, quoteData: any): Promise<any> => {
    const { data } = await axiosInstance.put(`/api/Quote/${id}`, quoteData);
    return data;
  },

  /**
   * Removes a quotation. `reason` is mandatory and the server refuses without it (400).
   *
   * A quote past DRAFT is WITHDRAWN, not deleted: the row stays on file with its R5 price
   * attestations and R7 validity extensions, and drops out of the quote list and the pipeline
   * stats. Only a clean draft — never attested, never extended, no order — is actually deleted,
   * and a tombstone is written either way. The response says which happened, so surface
   * `message` rather than assuming the record is gone.
   */
  removeQuote: async (
    id: number,
    reason: string,
    businessUnitId?: number,
  ): Promise<QuoteRemovalResult> => {
    const params: Record<string, string | number> = { reason };
    if (businessUnitId) params.businessUnitId = businessUnitId;
    const { data } = await axiosInstance.delete<QuoteRemovalResult>(`/api/Quote/${id}`, { params });
    return data;
  },
  
  /**
   * The commercial document itself. R5: the server refuses to render it unless the quote's
   * current prices are covered by a recorded price attestation (409, `priceAttestationRequired`).
   *
   * Because the request asks for a Blob, axios hands the ERROR body back as a Blob too, so the
   * refusal reason would otherwise be unreadable and every caller would fall back to a generic
   * "download failed". The body is decoded back to JSON here so the rep sees what to do next.
   */
  downloadPdf: async (id: number): Promise<Blob> => {
    try {
      const { data } = await axiosInstance.get(`/api/Quote/${id}/pdf`, { responseType: 'blob' });
      return data;
    } catch (error: any) {
      const body = error?.response?.data;
      if (body instanceof Blob) {
        try {
          error.response.data = JSON.parse(await body.text());
        } catch {
          // Not JSON (a proxy error page, an empty body). Leave it alone rather than
          // inventing a reason — the caller's fallback message is the honest answer.
        }
      }
      throw error;
    }
  },

  /**
   * Sends the quote email. Three 409s mean "nothing was sent", not "it failed":
   *  - WP-B3 `queuedForApproval` — parked in the Approvals inbox (below-floor pricing);
   *  - R5 `priceAttestationRequired` — the price source needs confirming (again);
   *  - R17 `taxDerivationRequired` — a line's output tax has not been calculated.
   * All three are surfaced as outcomes rather than thrown errors.
   */
  sendEmail: async (id: number, recipientEmail: string): Promise<QuoteSendOutcome> => {
    try {
      // 202 Accepted carries `{ queuedForDelivery, delivered, replayed }`. Read it: "queued" and
      // "delivered" are different facts and the rep is told different things for each.
      const { data } = await axiosInstance.post(`/api/Quote/${id}/email`, null, { params: { recipientEmail } });
      return {
        held: false,
        delivered: data?.delivered === true,
        queuedForDelivery: data?.queuedForDelivery === true,
      };
    } catch (error: any) {
      const data = error?.response?.data;
      // Only a string may become user-facing copy — an object here would render as
      // "[object Object]" at the call site.
      const asText = (...values: unknown[]) =>
        values.find((value): value is string => typeof value === 'string' && value.trim().length > 0);

      if (error?.response?.status === 409 && data?.priceAttestationRequired) {
        return { held: false, priceAttestationRequired: true, message: asText(data.message) };
      }
      if (error?.response?.status === 409 && data?.taxDerivationRequired) {
        return { held: false, taxDerivationRequired: true, message: asText(data.message) };
      }
      if (error?.response?.status === 409 && data?.queuedForApproval) {
        return { held: true, message: asText(data.summary, data.message), approvalId: data.approvalId };
      }
      throw error;
    }
  },

  // ==== Send readiness ====

  /**
   * Everything that would refuse this quote's send, answered BEFORE the send dialog opens.
   *
   * Half the send chain runs in a background worker whose refusals reach nobody: a draft with
   * no currency, a business unit with no legal name, and a tenant with no transmitting mailbox
   * all pass every synchronous check, produce a green "queued" toast, and die in the outbox.
   * The delivery idempotency key is fixed per quote, so a dead-lettered row then makes that
   * quote permanently unsendable.
   */
  getSendReadiness: async (id: number): Promise<QuoteSendReadiness> => {
    const { data } = await axiosInstance.get(`/api/Quote/${id}/send-readiness`);
    return data;
  },

  // ==== Price-provenance attestation (R5) ====

  /** Whether the quote may be sent, and the prices a fresh confirmation would cover. */
  getPriceAttestation: async (id: number): Promise<QuotePriceAttestationStatus> => {
    const { data } = await axiosInstance.get(`/api/Quote/${id}/price-attestation`);
    return data;
  },

  /** Records the rep's confirmation over the quote's current prices. */
  confirmPriceAttestation: async (
    id: number,
    source: PriceAttestationSource,
    sourceReference: string,
  ): Promise<QuotePriceAttestationStatus> => {
    const { data } = await axiosInstance.post(`/api/Quote/${id}/price-attestation`, {
      source,
      sourceReference,
    });
    return data;
  },

  // ==== Revisions-lite (WP-B4) ====

  /** Clones a non-draft quote as a new DRAFT revision; 409 when draft/superseded/locked. */
  revise: async (id: number): Promise<QuoteDTO> => {
    const { data } = await axiosInstance.post(`/api/Quote/${id}/revise`);
    return data;
  },

  getRevisionInfo: async (id: number): Promise<QuoteRevisionInfoDTO> => {
    const { data } = await axiosInstance.get(`/api/Quote/${id}/revisions`);
    return data;
  },

  resolveRevisionImpact: async (id: number): Promise<void> => {
    await axiosInstance.post(`/api/Quote/${id}/revision-impact/resolve`, null, {
      headers: { 'Idempotency-Key': crypto.randomUUID() },
    });
  },

  // ==== Reasoned validity extensions (R7) ====

  /**
   * Holds an already-sent quote's price open until a later date. The reason is mandatory and
   * is recorded against the quote, not merely logged. Does NOT create a revision — the buyer
   * is still looking at the same commercial offer.
   *
   * 400 = the date or reason is unusable; 409 = the quote's lifecycle does not allow it
   * (still a draft, already won/lost/expired, or superseded by a newer revision). Both carry
   * a message that says what to do instead.
   */
  extendValidity: async (
    id: number,
    validUntil: string,
    reason: string,
  ): Promise<QuoteValidityExtensionResult> => {
    const { data } = await axiosInstance.post(
      `/api/Quote/${id}/extend-validity`,
      { validUntil, reason },
      { headers: { 'Idempotency-Key': crypto.randomUUID() } },
    );
    return data;
  },

  /** Every recorded validity move on this quote, newest first (R7: the reason must be readable). */
  getValidityExtensions: async (id: number): Promise<QuoteValidityExtensionDTO[]> => {
    const { data } = await axiosInstance.get(`/api/Quote/${id}/validity-extensions`);
    return data;
  },

  transitionStatus: async (id: number, status: string, expectedVersion: number): Promise<unknown> => {
    const operationId = crypto.randomUUID();
    const { data } = await axiosInstance.post(`/api/Quote/${id}/status`, {
      targetStatusCode: status.toUpperCase(),
      expectedVersion,
      correlationId: operationId,
      idempotencyKey: `quote-${id}-${status.toLowerCase()}-${operationId}`,
    });
    return data;
  },

  // ==== Outcome capture (WP-A4) ====

  getOutcomeReasons: async (): Promise<OutcomeReasonDTO[]> => {
    const { data } = await axiosInstance.get('/api/Quote/outcome-reasons');
    return data;
  },

  setOutcome: async (
    id: number,
    outcome: QuoteOutcome,
    reasonCode?: string,
    note?: string,
  ): Promise<QuoteDTO> => {
    const { data } = await axiosInstance.post(`/api/Quote/${id}/outcome`, {
      outcome,
      reasonCode: reasonCode || undefined,
      note: note || undefined,
    });
    return data;
  },

  markResponded: async (id: number): Promise<void> => {
    await axiosInstance.post(`/api/Quote/${id}/mark-responded`);
  },
};

export default quoteService;
