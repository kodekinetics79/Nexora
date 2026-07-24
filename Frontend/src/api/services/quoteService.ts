import axiosInstance from '../axiosInstance';

export interface QuoteDTO {
  id: number;
  quoteNo: string;
  rfqId?: number;
  rfqNo?: string;
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
   * Sends the quote email. WP-B3: a 409 with queuedForApproval means nothing was
   * sent — the send is parked in the Approvals inbox (below-floor pricing) — and
   * is surfaced as `{ held: true }` rather than an error.
   */
  sendEmail: async (id: number, recipientEmail: string): Promise<QuoteSendOutcome> => {
    try {
      await axiosInstance.post(`/api/Quote/${id}/email`, null, { params: { recipientEmail } });
      return { held: false };
    } catch (error: any) {
      const data = error?.response?.data;
      if (error?.response?.status === 409 && data?.queuedForApproval) {
        return { held: true, message: data.summary || data.message, approvalId: data.approvalId };
      }
      throw error;
    }
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
