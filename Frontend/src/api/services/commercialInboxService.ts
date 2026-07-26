import axiosInstance from "../axiosInstance";

export interface CommercialInboxItem {
  id: string;
  sourceDocumentId: number;
  originalFileName: string;
  detectedMimeType: string;
  securityStatus: string;
  processingStatus: string;
  documentType: string;
  reviewStatus: string;
  confidence: number;
  classificationMethod: string;
  matches: { supplierRfqId?: number | null; sourcingCaseId?: number | null; supplierQuoteId?: number | null };
  supplierQuoteProjection: { state: string; isReady: boolean; blockingReasons: string[] };
  version: number;
  updatedOn: string;
}

export interface CommercialInboxResult {
  items: CommercialInboxItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

const commercialInboxService = {
  search: async (reviewStatus?: string): Promise<CommercialInboxResult> => {
    const response = await axiosInstance.get<CommercialInboxResult>("/api/commercial-inbox/classifications", {
      params: { page: 1, pageSize: 100, reviewStatus: reviewStatus || undefined },
    });
    return response.data;
  },
};

export default commercialInboxService;
