
import axiosInstance from '../axiosInstance';

export interface RfqResponseDTO {
    id: number;
    commercialCaseId?: number | null;
    commercialCaseReference?: string | null;
    nexoraSerial?: string | null;
    contactId?: number | null;
    contactName?: string | null;
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
        const response = await axiosInstance.post(`/api/Rfq/${id}/approve`, null, {
            params: { approvedBy, recipientEmail, emailSubject, emailBody, customerId }
        });
        return response.data;
    },
    delete: async (id: number, businessUnitId: number) => {
        await axiosInstance.delete(`/api/Rfq/${id}`, { params: { businessUnitId } });
    },
    create: async (data: any) => {
        const response = await axiosInstance.post("/api/Rfq", data);
        return response.data;
    },
    prepareQuoteDraft: async (id: number) => {
        const response = await axiosInstance.post(`/api/Rfq/${id}/prepare-quote-draft`);
        return response.data;
    },
};

export default rfqService;
