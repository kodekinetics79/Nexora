import axiosInstance from '../axiosInstance';

export interface CommercialCaseSearchResult {
  id: number;
  masterReference: string;
  leadId: number;
  customerRfqNumber?: string | null;
  buyerName?: string | null;
  customerEmail?: string | null;
  status?: string | null;
  createdOn: string;
  rfqCount: number;
  quoteCount: number;
  orderCount: number;
  shipmentCount: number;
  matchReason: string;
}

export interface CommercialCaseDocument {
  documentType: 'Lead' | 'RFQ' | 'Quote' | 'Order' | 'Shipment' | string;
  documentId: number;
  reference: string;
  status?: string | null;
  occurredOn?: string | null;
  parentDocumentId?: number | null;
}

export interface CommercialCaseStatusEvent {
  id: number;
  eventType: string;
  previousStatus?: string | null;
  newStatus?: string | null;
  changedBy?: string | null;
  actorSource: string;
  changedOn: string;
  reason?: string | null;
  aggregateType?: string | null;
  correlationId?: string | null;
  requestReference?: string | null;
  policyVersion?: string | null;
  reasonCode?: string | null;
}

export interface CommercialCaseDetail {
  id: number;
  masterReference: string;
  allocationNumber: number;
  businessUnitId: number;
  createdOn: string;
  leadId: number;
  customerRfqNumber?: string | null;
  buyerName?: string | null;
  customerEmail?: string | null;
  opportunityNumber?: string | null;
  currentStatus?: string | null;
  documents: CommercialCaseDocument[];
  statusHistory: CommercialCaseStatusEvent[];
}

const commercialCaseService = {
  search: async (query: string, limit = 20): Promise<CommercialCaseSearchResult[]> => {
    const response = await axiosInstance.get<CommercialCaseSearchResult[]>('/api/commercial-cases/search', {
      params: { q: query, limit },
    });
    return response.data;
  },

  getById: async (id: number): Promise<CommercialCaseDetail> => {
    const response = await axiosInstance.get<CommercialCaseDetail>(`/api/commercial-cases/${id}`);
    return response.data;
  },
};

export default commercialCaseService;
