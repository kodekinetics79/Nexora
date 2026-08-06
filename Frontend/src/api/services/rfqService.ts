
import axiosInstance from '../axiosInstance';

export interface RfqResponseDTO {
    id: number;
    commercialCaseId?: number | null;
    commercialCaseReference?: string | null;
    nexoraSerial?: string | null;
    contactId?: number | null;
    contactName?: string | null;
    accountOwnerName?: string | null;
    opportunityOwnerName?: string | null;
    rfqno: string;
    buyersName?: string;
    recDate: string;
    bidClosingDate?: string;
    biddingDecision?: string;
    acknowledgmentDate?: string;
    subDate?: string;
    headerRemarks?: string;
    opportunityNo?: string;
    noOfLineItems?: number;
    rfqtype?: string;
    rfqtypeId?: number;
    durationAgreement?: string;
    leadId?: number;
    activeLeadRevision: number;
    createdBy: string;
    createdDate: string;
    modifiedBy?: string;
    modifiedDate?: string;
    businessUnitId: number;
    businessUnitName?: string;
    rfqstatusId?: number;
    rfqstatusValue?: string;
    customerId?: number;
    customerName?: string;
    customerEmail?: string;
    leadEmail?: string;
    rfqitems: RfqitemResponseDTO[];
    readiness: string;
}

export interface RfqitemResponseDTO {
    id: number;
    rfqid: number;
    companyRef?: string;
    customerAccountPortalId?: string;
    customerRfqno?: string;
    itemMaterialCode?: string;
    lineItemNo?: string;
    productId?: number;
    productName?: string;
    commodityProduct?: string;
    productShortName?: string;
    productShortDescription?: string;
    alternative?: string;
    buyerName?: string;
    currency?: string;
    currencyId?: number;
    unitOfMeasure?: string;
    uomId?: number;
    unitPrice?: number;
    quantity: number;
    storageLocation?: string;
    warehouseId?: number;
    warehouseName?: string;
    manufacturerName?: string;
    manufacturerPartNumber?: string;
    supplierId?: number;
    supplierName?: string;
    alternateProductName?: string;
    alternatePartNumber?: string;
    itemText?: string;
    materialPotext?: string;
    leadTime?: number;
    requiredDesiredDate?: string;
    receivedDate?: string;
    bidClosingDateLine: string;
    createdBy: string;
    createdDate: string;
    modifiedBy?: string;
    modifiedDate?: string;
    aiconfidence?: number;
    supplierQuotedItemId?: number;
  /** 'Pending' | 'Quote' | 'NoQuote'. A line nobody has decided is Pending, never Quote. */
  participationDecision: string;
  /** Mandatory whenever participationDecision is 'NoQuote'. */
  noQuoteReason?: string | null;
  participationDecidedBy?: string | null;
  participationDecidedOn?: string | null;
}

export interface PaginatedRfqResponseDTO {
    items: RfqResponseDTO[];
    totalItems: number;
    pageNumber: number;
    pageSize: number;
    totalPages: number;
}

export interface RfqFilterParams {
    pageNumber: number;
    pageSize: number;
    search?: string;
    isActive?: boolean;
    businessUnitId?: number;
    assignedToId?: number;
    createdBy?: string;
    rfqStatusId?: number;
    rfqStatusCode?: string;
    readiness?: string;
}

/** Mirrors backend RfqitemCreateRequestDTO. `quantity` is [Required] server-side and must be >= 1. */
export interface RfqitemCreatePayload {
    companyRef?: string | null;
    customerAccountPortalId?: string | null;
    customerRfqno?: string | null;
    itemMaterialCode?: string | null;
    lineItemNo?: string | null;
    productId?: number | null;
    /** Carried for governed sourcing lines; ignored by the create endpoint today. */
    supplierQuotedItemId?: number | null;
    commodityProduct?: string | null;
    productShortName?: string | null;
    productShortDescription?: string | null;
    alternative?: string | null;
    buyerName?: string | null;
    currency?: string | null;
    currencyId?: number | null;
    unitOfMeasure?: string | null;
    uomId?: number | null;
    unitPrice?: number | null;
    quantity: number;
    storageLocation?: string | null;
    warehouseId?: number | null;
    manufacturerName?: string | null;
    manufacturerPartNumber?: string | null;
    supplierId?: number | null;
    alternateProductName?: string | null;
    alternatePartNumber?: string | null;
    itemText?: string | null;
    materialPotext?: string | null;
    leadTime?: number | null;
    requiredDesiredDate?: string | null;
    receivedDate?: string | null;
    bidClosingDateLine?: string | null;
    aiconfidence?: number | null;
}

/**
 * Mirrors backend RfqCreateRequestDTO. `recDate` is a non-nullable DateTime server-side — always
 * send a real date. `leadId` is optional: when omitted the backend links the RFQ to a governed
 * shell lead so it still belongs to a commercial case (the response carries the linkage).
 */
export interface RfqCreatePayload {
    rfqno?: string | null;
    buyersName?: string | null;
    recDate: string;
    bidClosingDate?: string | null;
    biddingDecision?: string | null;
    acknowledgmentDate?: string | null;
    subDate?: string | null;
    headerRemarks?: string | null;
    opportunityNo?: string | null;
    rfqtype?: string | null;
    rfqtypeId?: number | null;
    durationAgreement?: string | null;
    leadId?: number | null;
    rfqstatusId?: number | null;
    customerId?: number | null;
    contactId?: number | null;
    rfqitems: RfqitemCreatePayload[];
}

const rfqService = {
    getAll: async (params: RfqFilterParams): Promise<PaginatedRfqResponseDTO> => {
        const response = await axiosInstance.get<PaginatedRfqResponseDTO>("/api/Rfq", { params });
        return response.data;
    },
    getById: async (id: number, businessUnitId: number) => {
        const response = await axiosInstance.get<RfqResponseDTO>(`/api/Rfq/${id}`, { params: { businessUnitId } });
        return response.data;
    },
    approve: async (id: number, approvedBy: string, recipientEmail?: string, emailSubject?: string, emailBody?: string, customerId?: number) => {
        void approvedBy;
        const response = await axiosInstance.post(`/api/Rfq/${id}/approve`, {
            recipientEmail, emailSubject, emailBody, customerId,
        });
        return response.data;
    },
    delete: async (id: number, businessUnitId: number) => {
        await axiosInstance.delete(`/api/Rfq/${id}`, { params: { businessUnitId } });
    },
    create: async (data: RfqCreatePayload): Promise<RfqResponseDTO> => {
        const response = await axiosInstance.post<RfqResponseDTO>("/api/Rfq", data);
        return response.data;
    },
    /**
   * Records whether Nexora will quote one RFQ line. A No-Quote requires a reason — the rule is
   * enforced in the backend domain, so this call will be refused without one.
   */
  setLineParticipation: async (
    rfqId: number,
    lineId: number,
    decision: 'Pending' | 'Quote' | 'NoQuote',
    reason?: string,
  ) => {
    const response = await axiosInstance.post(
      `/api/Rfq/${rfqId}/lines/${lineId}/participation`,
      { decision, reason },
    );
    return response.data;
  },

  prepareQuoteDraft: async (id: number) => {
        const response = await axiosInstance.post(`/api/Rfq/${id}/prepare-quote-draft`);
        return response.data;
    },
};

export default rfqService;
