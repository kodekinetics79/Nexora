import axiosInstance from '../axiosInstance';

// ─── DTOs ────────────────────────────────────────────────────────────────────

export interface ProductAttachmentDTO {
  attachmentId: number;
  fileName: string;
  location: string;
  description?: string;
}

export interface ProductDTO {
  id: number;
  docId?: string;
  productName?: string;
  partNo: string;
  modelNo?: string;
  description?: string;
  categoryId?: number;
  categoryName?: string;
  subCategoryId?: number;
  subCategoryName?: string;
  qtyOnHand: number;
  reorderPoint: number;
  uomId?: number;
  uomName?: string;
  unitCost?: number;
  sellingPrice?: number;
  finalLandedCost?: number;
  finalSalesPrice?: number;
  warehouseId?: number;
  warehouseName?: string;
  preferredSupplierId?: number;
  preferredSupplierName?: string;
  preferredSupplierEmail?: string;
  batchTracking?: boolean;
  serialTracking?: boolean;
  expirationDate?: string;
  height?: number;
  width?: number;
  depth?: number;
  weight?: number;
  dimensions?: string;
  barcode?: string;
  qrcode?: string;
  leadTime?: number;
  hscode?: string;
  countryOfOrigin?: string;
  buid?: number;
  businessUnitName?: string;
  isActive: boolean;
  isCatalogItem?: boolean;
  createdBy: string;
  createdOn: string;
  modifiedBy?: string;
  modifiedOn?: string;
  images: ProductAttachmentDTO[];
  attachments: ProductAttachmentDTO[];
}

export interface PaginatedProductResponse {
  items: ProductDTO[];
  totalItems: number;
  pageNumber: number;
  pageSize: number;
  totalPages: number;
}

export interface ProductFilters {
  businessUnitId?: number;
  pageNumber?: number;
  pageSize?: number;
  search?: string;
  isActive?: boolean;
}

export interface ProductLookup {
  id: number;
  name: string;
}

// ─── Service ─────────────────────────────────────────────────────────────────

const productService = {
  getAll: async (params: ProductFilters): Promise<PaginatedProductResponse> => {
    const response = await axiosInstance.get<PaginatedProductResponse>('/api/Product', { params });
    return response.data;
  },

  getById: async (id: number): Promise<ProductDTO> => {
    const response = await axiosInstance.get<ProductDTO>(`/api/Product/${id}`);
    return response.data;
  },

  create: async (data: FormData): Promise<ProductDTO> => {
    const response = await axiosInstance.post<ProductDTO>('/api/Product', data, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
    return response.data;
  },

  update: async (id: number, data: FormData): Promise<ProductDTO> => {
    const response = await axiosInstance.put<ProductDTO>(`/api/Product/${id}`, data, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
    return response.data;
  },

  delete: async (id: number): Promise<void> => {
    await axiosInstance.delete(`/api/Product/${id}`);
  },

  // ─── Lookups ─────────────────────────────────────────────────────────────

  getCategories: async (): Promise<any[]> => {
    const r = await axiosInstance.get('/api/Product/lookups/product-categories');
    return r.data;
  },

  getSubCategories: async (): Promise<any[]> => {
    const r = await axiosInstance.get('/api/Product/lookups/product-subcategories');
    return r.data;
  },

  getWarehouses: async (): Promise<any[]> => {
    const r = await axiosInstance.get('/api/Product/lookups/warehouses');
    return r.data;
  },

  getUoms: async (): Promise<any[]> => {
    const r = await axiosInstance.get('/api/Product/lookups/uoms');
    return r.data;
  },

  getSuppliers: async (): Promise<any[]> => {
    const r = await axiosInstance.get('/api/Product/lookups/suppliers');
    return r.data;
  },

  // ─── Upload / Export ──────────────────────────────────────────────────
  downloadTemplate: () =>
    axiosInstance.get('/api/ProductUploader/download-template', { responseType: 'blob' }),

  uploadTemplate: (file: File) => {
    const fd = new FormData();
    fd.append('file', file);
    return axiosInstance.post('/api/ProductUploader/upload-template', fd, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
  },

  export: () =>
    axiosInstance.get('/api/ProductUploader/export', { responseType: 'blob' }),

  getPurchaseHistory: async (id: number): Promise<any> => {
    const r = await axiosInstance.get(`/api/Product/${id}/purchase-history`);
    return r.data;
  },

  matchProduct: async (query: { name?: string; partNo?: string; manufacturer?: string; businessUnitId?: number }) => {
    const response = await axiosInstance.post<{
      hasExactMatch: boolean;
      exactMatch: ProductDTO | null;
      fuzzyMatches: ProductDTO[];
    }>('/api/Product/match-product', query);
    return response.data;
  },
};

export default productService;
