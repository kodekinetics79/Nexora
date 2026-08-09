import axiosInstance from '../axiosInstance';

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
  quoteItems: any[];
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
}

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

  delete: async (id: number, businessUnitId?: number): Promise<void> => {
    const params = businessUnitId ? { businessUnitId } : {};
    await axiosInstance.delete(`/api/Quote/${id}`, { params });
  },
  
  downloadPdf: async (id: number): Promise<Blob> => {
    const { data } = await axiosInstance.get(`/api/Quote/${id}/pdf`, { responseType: 'blob' });
    return data;
  },

  /**
   * Sends the quote email. Two 409s mean "nothing was sent", not "it failed":
   *  - WP-B3 `queuedForApproval` — parked in the Approvals inbox (below-floor pricing);
   *  - R5 `priceAttestationRequired` — the price source needs confirming (again).
   * Both are surfaced as outcomes rather than thrown errors.
   */
  sendEmail: async (id: number, recipientEmail: string): Promise<QuoteSendOutcome> => {
    try {
      await axiosInstance.post(`/api/Quote/${id}/email`, null, { params: { recipientEmail } });
      return { held: false };
    } catch (error: any) {
      const data = error?.response?.data;
      // Only a string may become user-facing copy — an object here would render as
      // "[object Object]" at the call site.
      const asText = (...values: unknown[]) =>
        values.find((value): value is string => typeof value === 'string' && value.trim().length > 0);

      if (error?.response?.status === 409 && data?.priceAttestationRequired) {
        return { held: false, priceAttestationRequired: true, message: asText(data.message) };
      }
      if (error?.response?.status === 409 && data?.queuedForApproval) {
        return { held: true, message: asText(data.summary, data.message), approvalId: data.approvalId };
      }
      throw error;
    }
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
