import axiosInstance from '../axiosInstance';

export interface LeadFilters {
  businessUnitId?: number;
  pageNumber?: number;
  pageSize?: number;
  id?: number;
  rfqno?: string;
  buyersName?: string;
  leadSource?: string;
  startDate?: string;
  endDate?: string;
  emailSource?: string;
  clientemail?: string;
}

export interface LeadResponseDTO {
  id: number;
  rfqno: string;
  buyersName: string;
  leadSource: string;
  recDate: string;
  bidClosingDate: string;
  emailSource: string;
  clientemail: string;
  status: string;
  isAccepted: boolean;
  isRejected: boolean;
  aiconfidence: number;
  itemCount: number;
  headerRemarks?: string;
  opportunityNo?: string;
  biddingDecision?: string;
  acknowledgmentDate?: string;
  subDate?: string;
  rfqtype?: string;
  durationAgreement?: string;
  createdBy?: string;
  createdDate?: string;
  businessUnitId: number;
  businessUnitName?: string;
  assignedToFullName?: string;
  leadItems?: LeadItemResponseDTO[];
  attachments?: AttachmentResponseDTO[];
}

export interface LeadItemResponseDTO {
  id: number;
  lineItemNo?: string;
  itemMaterialCode?: string;
  productShortName?: string;
  productShortDescription?: string;
  quantity?: number;
  unitOfMeasure?: string;
  currency?: string;
  unitPrice?: number;
  aiconfidence: number;
  manufacturerName?: string;
  manufacturerPartNumber?: string;
  customerRfqno?: string;
  storageLocation?: string;
  commodityProduct?: string;
  // Additional extraction fields surfaced by GET /api/Lead/{id} and edited in
  // the Extraction Review workbench (all optional / additive).
  alternateProductName?: string;
  alternatePartNumber?: string;
  itemText?: string;
  leadTime?: string;
}

export interface AttachmentResponseDTO {
  id: number;
  fileName: string;
  fileSize: number;
  mimeType: string;
}

export interface AcceptedLeadResponseDTO {
  id: number;
  leadId: number;
  rfqno: string;
  buyersName: string;
  acceptedDate: string;
  assignedToFullName?: string;
  assignedToId?: number;
  assignedOn?: string;
  comment?: string;
  // Add other fields as needed
}

export interface AcceptedLeadFullResponseDTO {
  id: number;
  rfqno?: string;
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
  durationAgreement?: string;
  aiconfidence?: number;
  leadSource: string;
  emailSource?: string;
  clientemail?: string;
  createdDate: string;
  modifiedDate?: string;
  assignedToId?: number;
  assignedToFullName?: string;
  assignedOn?: string;
  assignComment?: string;
  leadItems: AcceptedLeadItemDTO[];
  attachments: AttachmentResponseDTO[];
}

export interface AcceptedLeadItemDTO {
  id: number;
  companyRef?: string;
  customerAccountPortalId?: string;
  customerRfqno?: string;
  itemMaterialCode?: string;
  commodityProduct?: string;
  buyerName?: string;
  lineItemNo?: string;
  productShortName?: string;
  alternative?: string;
  productShortDescription?: string;
  currency?: string;
  unitOfMeasure?: string;
  unitPrice?: number;
  quantity: number;
  storageLocation?: string;
  manufacturerName?: string;
  manufacturerPartNumber?: string;
  alternateProductName?: string;
  alternatePartNumber?: string;
  itemText?: string;
  materialPotext?: string;
  leadTime?: string;
  receivedDate?: string;
  bidClosingDateLine?: string;
  aiconfidence?: number;
}

export interface PaginatedResponse<T> {
  items: T[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
}

const leadService = {
  getAll: async (filters: LeadFilters): Promise<PaginatedResponse<LeadResponseDTO>> => {
    const r = await axiosInstance.get('/api/Lead', { params: filters });
    return r.data;
  },

  getById: async (id: number): Promise<LeadResponseDTO> => {
    const r = await axiosInstance.get(`/api/Lead/${id}`);
    return r.data;
  },

  getOutstandingLeads: async (params: any): Promise<PaginatedResponse<AcceptedLeadResponseDTO>> => {
    const r = await axiosInstance.get('/api/UnAssignedLead', { params });
    return r.data;
  },

  getAssignedLeads: async (params: any): Promise<PaginatedResponse<AcceptedLeadResponseDTO>> => {
    const r = await axiosInstance.get('/api/UnAssignedLead/assigned', { params });
    return r.data;
  },

  getAcceptedLeadById: async (id: number): Promise<AcceptedLeadFullResponseDTO> => {
    const r = await axiosInstance.get(`/api/UnAssignedLead/${id}`);
    return r.data;
  },

  acceptLead: async (id: number) => {
    return axiosInstance.post(`/api/Lead/accept/${id}`);
  },

  rejectLead: async (id: number, reasonId: number) => {
    return axiosInstance.post(`/api/Lead/reject/${id}`, null, { params: { reasonId } });
  },

  getRejectionReasons: async () => {
    const r = await axiosInstance.get('/api/Lead/rejection-reasons');
    return r.data;
  },

  getStats: async () => {
    const r = await axiosInstance.get('/api/Lead/stats');
    return r.data;
  },

  getUsersForAssignment: async (buid: number) => {
    const r = await axiosInstance.get('/api/UnAssignedLead/users-for-assignment', { params: { businessUnitId: buid } });
    return r.data;
  },

  assignLead: async (data: { leadId: number; assignedToUserId: number; comment?: string }) => {
    return axiosInstance.post('/api/UnAssignedLead/assign', data);
  },

  // Uploads
  uploadManual: async (formData: FormData) => {
    return axiosInstance.post('/api/ManualUpload/upload', formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
  },

  uploadRfqExcel: async (formData: FormData) => {
    return axiosInstance.post('/api/ManualUpload/upload-rfq-excel', formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
  },

  uploadBulkLeads: async (formData: FormData) => {
    return axiosInstance.post('/api/LeadUploader/upload', formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
  },
  uploadToFolder: async (formData: FormData, folderType: string): Promise<any> => {
    const r = await axiosInstance.post(`/api/Email/upload-leads-folder?folderType=${folderType}`, formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
    return r.data;
  },

  processAllFolderLeads: async (): Promise<any> => {
    const r = await axiosInstance.post('/api/Email/process-all-folder-leads');
    return r.data;
  },

  fetchEmails: async (): Promise<any> => {
    const r = await axiosInstance.post('/api/Email/fetch');
    return r.data;
  }
};

export default leadService;
