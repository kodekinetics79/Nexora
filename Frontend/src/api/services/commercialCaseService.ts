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

/**
 * How a listed document relates to the case it appears under.
 *
 * Membership is decided by what the document DECLARES (its CommercialCaseId, or its Nexora
 * Serial where the surrogate key does not exist yet). `Reconciled` means the legacy foreign-key
 * chain agrees; `ChainBroken` means the document states this case but the chain no longer reaches
 * it — the declaration wins, and the broken link is reported as a gap.
 */
export type CommercialCaseLinkState = 'Reconciled' | 'ChainBroken' | 'DeclaredOnly';

/**
 * `CustomerOriginMissing` is not a case-linkage gap like the other three: the document states this
 * case correctly. It is a supplier purchase order raised against customer demand that names no
 * client PO, sales order or quotation, so the customer behind it can only be inferred by re-joining
 * through the RFQ. STOCK replenishment orders never raise it — they have no customer.
 */
export type CommercialCaseGapKind =
  | 'UnlinkedDocument'
  | 'ConflictingCase'
  | 'ChainBroken'
  | 'CustomerOriginMissing';

export interface CommercialCaseDocument {
  documentType: 'Lead' | 'RFQ' | 'Quote' | 'Order' | 'Shipment' | string;
  documentId: number;
  reference: string;
  status?: string | null;
  occurredOn?: string | null;
  parentDocumentId?: number | null;
  linkState: CommercialCaseLinkState;
}

/**
 * A disagreement between what a document declares and what the document chain says. Surfaced
 * rather than swallowed: a timeline that silently drops an unlinked document makes an incomplete
 * spine look complete.
 */
export interface CommercialCaseTraceabilityGap {
  documentType: string;
  documentId: number;
  reference: string;
  gapKind: CommercialCaseGapKind;
  declaredCommercialCaseId?: number | null;
  detail: string;
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
  traceabilityGaps: CommercialCaseTraceabilityGap[];
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
