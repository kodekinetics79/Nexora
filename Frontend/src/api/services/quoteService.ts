import axiosInstance from '../axiosInstance';

export interface QuoteDTO {
  id: number;
  quoteNo: string;
  rfqId?: number;
  rfqNo?: string;
  customerId?: number;
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

  sendEmail: async (id: number, recipientEmail: string): Promise<void> => {
    await axiosInstance.post(`/api/Quote/${id}/email`, null, { params: { recipientEmail } });
  },

  transitionStatus: async (id: number, status: string, modifiedBy: string): Promise<QuoteDTO> => {
    const { data } = await axiosInstance.post(`/api/Quote/${id}/status`, null, {
      params: { status, modifiedBy }
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
