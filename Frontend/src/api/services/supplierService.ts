import axiosInstance from '../axiosInstance';

export interface SupplierDTO {
  id: number;
  docId?: string;
  name: string;
  contactEmail?: string;
  imageUrl?: string;
  paymentTerms?: string;
  addressLine1?: string;
  addressLine2?: string;
  cityId?: number;
  cityName?: string;
  countryId?: number;
  countryName?: string;
  postalCode?: string;
  successRate?: number;
  avgResponseTime?: number;
  tags?: string;
  comments?: string;
  currencyId?: number;
  currencyName?: string;
  buid?: number;
  businessUnitName?: string;
  isActive?: boolean;
  createdBy?: string;
  createdOn?: string;
  modifiedBy?: string;
  modifiedOn?: string;
  governanceStatus: string;
  verificationStatus: string;
  complianceStatus: string;
  riskStatus: string;
  readinessStatus: string;
  governanceReviewedBy?: string;
  governanceReviewedOn?: string;
  concurrencyToken?: string;
}

export interface GovernSupplierRequest {
  governanceStatus: string;
  verificationStatus: string;
  complianceStatus: string;
  riskStatus: string;
  readinessStatus: string;
  expectedConcurrencyToken: string;
  reason: string;
}

export interface PaginatedSupplierResponse {
  items: SupplierDTO[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
}

export interface SupplierFilters {
  pageNumber?: number;
  pageSize?: number;
  name?: string;
  contactEmail?: string;
  isActive?: boolean;
  businessUnitId?: number;
}

const supplierService = {
  getAll: async (params: SupplierFilters): Promise<PaginatedSupplierResponse> => {
    const r = await axiosInstance.get('/api/Supplier', { params });
    return r.data;
  },

  getById: async (id: number): Promise<SupplierDTO> => {
    const r = await axiosInstance.get(`/api/Supplier/${id}`);
    return r.data;
  },

  create: async (data: FormData): Promise<SupplierDTO> => {
    const r = await axiosInstance.post('/api/Supplier', data, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
    return r.data;
  },

  update: async (id: number, data: FormData): Promise<void> => {
    await axiosInstance.put(`/api/Supplier/${id}`, data, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
  },

  delete: async (id: number): Promise<void> => {
    await axiosInstance.delete(`/api/Supplier/${id}`);
  },

  // Upload / Export
  downloadTemplate: () =>
    axiosInstance.get('/api/SupplierUploader/download-template', { responseType: 'blob' }),

  uploadTemplate: (file: File) => {
    const fd = new FormData();
    fd.append('file', file);
    return axiosInstance.post('/api/SupplierUploader/upload-template', fd, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
  },

  export: () =>
    axiosInstance.get('/api/SupplierUploader/export', { responseType: 'blob' }),

  searchSuppliers: async (searchTerm: string, productCategory: string, businessUnitId: number): Promise<SupplierDTO[]> => {
    const response = await axiosInstance.get<SupplierDTO[]>("/api/Supplier/search", {
      params: { searchTerm, productCategory, businessUnitId }
    });
    return response.data;
  },

  searchWebSuppliers: async (query: string): Promise<any[]> => {
    const response = await axiosInstance.get<any[]>("/api/Supplier/web-search", {
      params: { query }
    });
    return response.data;
  },

  govern: async (id: number, request: GovernSupplierRequest): Promise<SupplierDTO> => {
    const response = await axiosInstance.post(`/api/suppliers/${id}/governance`, request, {
      headers: {
        'Idempotency-Key': crypto.randomUUID(),
        'X-Correlation-ID': crypto.randomUUID(),
      },
    });
    return response.data;
  },
};

export default supplierService;
