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
    // Advanced simulation of an intelligent web search
    await new Promise(resolve => setTimeout(resolve, 1500));
    
    // Normalize query
    const term = query.trim().split(' ').slice(0, 3).join(' ');
    const domain = query.replace(/\s+/g, '').toLowerCase();

    return [
      {
        id: -100,
        name: `${term} Solutions Global`,
        contactEmail: `sales@${domain}-solutions.com`,
        addressLine1: "Tech Park, Suite 400",
        city: "San Jose",
        countryName: "USA",
        tags: `External, ${term}, Preferred`,
        isExternal: true
      },
      {
        id: -200,
        name: `Integrated ${term} Group`,
        contactEmail: `procurement@${domain}-group.net`,
        addressLine1: "Industrial Zone B",
        city: "Dubai",
        countryName: "UAE",
        tags: `External, ${term}, Global`,
        isExternal: true
      },
      {
        id: -300,
        name: `${term} Distribution Ltd.`,
        contactEmail: `info@${domain}dist.co.uk`,
        addressLine1: "Commerce House",
        city: "London",
        countryName: "UK",
        tags: `External, ${term}, Logistics`,
        isExternal: true
      }
    ];
  },
};

export default supplierService;
