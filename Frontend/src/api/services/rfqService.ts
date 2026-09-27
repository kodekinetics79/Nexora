
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
    customerRfqReference?: string | null;
    requiredDeliveryDate?: string | null;
    deliveryLocation?: string | null;
    agreementReference?: string | null;
    bidClosingDateHijri?: string | null;
    inquiryType?: string | null;
    leadId?: number;
    activeLeadRevision: number;
    promotionId?: number | null;
    sourceLeadRevisionId?: number | null;
    sourceLeadRevisionNumber?: number | null;
    participationDecisionId?: number | null;
    participationVersion?: number | null;
    promotedAtUtc?: string | null;
    promotedBy?: string | null;
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
    /** False when the linked part was kept out of the catalogue. */
    productIsCatalogItem?: boolean | null;
    productResolvedBy?: string | null;
    productResolvedOn?: string | null;
    productResolutionReason?: string | null;
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
    extraFields?: string | null;
    /** Set when the part asked for is obsolete or discontinued and something else is being offered. */
    offeredPartNumber?: string | null;
    offeredMakerName?: string | null;
    offeredKind?: string | null;
    offeredNote?: string | null;
    offeredSpecs?: string | null;
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
    sourceLeadItemRevisionId?: number | null;
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

/** Mirrors backend RfqitemCreateRequestDTO. `quantity` is [Required] server-side and must be positive. */
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
    /** The RFQ's latest quote, or null when none has been started. state: DRAFT | SENT | DECIDED. */
    getLatestQuote: async (rfqId: number): Promise<{ quoteId: number; quoteNo: string; state: 'DRAFT' | 'SENT' | 'DECIDED' } | null> => {
        const response = await axiosInstance.get(`/api/rfq/${rfqId}/latest-quote`);
        return response.status === 204 || !response.data ? null : response.data;
    },

    /** The part actually offered for an RFQ line when the one asked for is obsolete or discontinued. Empty body clears it. */
    saveOfferedPart: async (rfqId: number, itemId: number, body: {
        partNumber?: string; makerName?: string | null; kind?: 'REPLACEMENT' | 'EQUIVALENT'; note?: string | null; specs?: string | null; productId?: number | null;
    }) => {
        const response = await axiosInstance.put(`/api/rfq/${rfqId}/items/${itemId}/offered-part`, body);
        return response.data as { offeredPartNumber?: string | null; productId?: number | null };
    },

    /** One draft quote line: ESTIMATE with a price, TO_FOLLOW, NOT_QUOTED with a reason, or null for a plain price. */
    saveLinePricing: async (quoteId: number, lineId: number, body: { status: 'ESTIMATE' | 'TO_FOLLOW' | 'NOT_QUOTED' | null; note?: string | null; unitPrice?: number | null }) => {
        const response = await axiosInstance.put(`/api/rfq/quotes/${quoteId}/lines/${lineId}/pricing`, body);
        return response.data as { quoteId: number };
    },

    /** Currency (only while the quote has none) and validity date of a draft quote. */
    saveQuoteTerms: async (quoteId: number, terms: { currencyId?: number | null; validUntil?: string | null }) => {
        const response = await axiosInstance.put(`/api/rfq/quotes/${quoteId}/terms`, terms);
        return response.data as { quoteId: number; currencyId?: number | null; validUntil?: string | null };
    },

    /** The makers the customer accepts for one line, separated by ";" ("ABB 1SDA; GE THQL32010; Eaton"). Empty clears the list. */
    saveAcceptedMakers: async (rfqId: number, itemId: number, acceptedMakers: string): Promise<{ acceptedMakers: string[] }> => {
        const response = await axiosInstance.put<{ acceptedMakers: string[] }>(`/api/rfq/${rfqId}/items/${itemId}/makers`, { acceptedMakers });
        return response.data;
    },

    getAll: async (params: RfqFilterParams): Promise<PaginatedRfqResponseDTO> => {
        const response = await axiosInstance.get<PaginatedRfqResponseDTO>("/api/Rfq", { params });
        return response.data;
    },
    getById: async (id: number, businessUnitId: number) => {
        const response = await axiosInstance.get<RfqResponseDTO>(`/api/Rfq/${id}`, { params: { businessUnitId } });
        return response.data;
    },
    /** Downloads the RFQ's lines as an Excel sheet and triggers a browser save. */
    downloadLinesExcel: async (id: number, rfqNo?: string): Promise<void> => {
        const response = await axiosInstance.get(`/api/Rfq/${id}/lines.xlsx`, { responseType: 'blob' });
        const url = window.URL.createObjectURL(new Blob([response.data], {
            type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
        }));
        const link = document.createElement('a');
        link.href = url;
        link.download = `${(rfqNo || `RFQ-${id}`).replace(/[^\w.-]+/g, '-')}-lines.xlsx`;
        document.body.appendChild(link);
        link.click();
        link.remove();
        window.URL.revokeObjectURL(url);
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
  prepareQuoteDraft: async (id: number) => {
        const response = await axiosInstance.post(`/api/Rfq/${id}/prepare-quote-draft`);
        return response.data;
    },
  resolveLineProduct: async (id: number, lineId: number, productId: number, reason: string) => {
        const response = await axiosInstance.post(`/api/Rfq/${id}/lines/${lineId}/resolve-product`, {
            productId,
            reason,
        });
        return response.data;
    },
};

export default rfqService;
