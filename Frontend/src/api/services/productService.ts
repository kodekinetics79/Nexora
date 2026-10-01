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
  priceCurrencyId?: number | null;
  /** Currency for both landed cost and sale price; absent while the product is deliberately unpriced. */
  priceCurrencyCode?: string | null;
  reservedQuantity?: number;
  availableQuantity?: number;
  incomingQuantity?: number;
  nextIncomingOn?: string | null;
  incomingCommitmentCount?: number;
  incomingWarehouseCount?: number;
  reorderGap?: number;
  reorderThreshold?: number;
  reorderStatus?: 'OUT_OF_STOCK' | 'BELOW_MINIMUM' | 'REORDER_POINT' | 'OVERSTOCK' | null;
  reorderWarehouseSummary?: string | null;
  warehouseCount?: number;
  stockLocationSummary?: string | null;
  materialLotCount?: number;
  quarantinedLotCount?: number;
  expiredCertificateCount?: number;
  materialLotRemainingQuantity?: number;
  finalLandedCost?: number;
  finalSalesPrice?: number;
  /** Latest committed supplier PO unit cost, in its own transaction currency. */
  lastPurchaseCost?: number | null;
  lastPurchaseCurrencyCode?: string | null;
  lastPurchaseOn?: string | null;
  warehouseId?: number;
  warehouseName?: string;
  preferredSupplierId?: number;
  preferredSupplierName?: string;
  preferredSupplierEmail?: string;
  preferredSupplierTier?: string;
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

export interface ProductMatchSuggestion {
  productId?: number;
  id?: number;
  productName: string;
  partNo: string;
  manufacturer?: string;
  manufacturerName?: string;
  description?: string;
  qtyOnHand?: number;
  unitCost?: number;
  costCurrencyCode?: string;
  sellingPrice?: number;
  finalLandedCost?: number;
  finalSalesPrice?: number;
  preferredSupplierId?: number;
  preferredSupplierName?: string;
  preferredSupplierEmail?: string;
  availableToPromise?: number;
  incomingAvailable?: number;
  projectedShortage?: number;
  availabilityStatus?: string;
  leadTimeDays?: number | null;
  expectedAvailableOn?: string | null;
  decisionState?: string;
  evidenceReference?: string | null;
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
  stock?: 'all' | 'in-stock' | 'out-of-stock' | 'low-stock';
  warehouseId?: number;
  sortBy?: 'name' | 'partNo' | 'unit' | 'onHand' | 'available' | 'lastPurchaseCost' | 'landedCost' | 'salePrice' | 'currency';
  sortDirection?: 'asc' | 'desc';
}

export interface ProductLookup {
  id: number;
  name: string;
}

export interface ProductCurrencyLookup {
  id: number;
  code: string;
  isBase: boolean;
}

export interface ProductWarehouseLookup {
  id: number;
  warehouseName: string;
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

  /** Puts a part into the catalogue (true) or keeps it out (false) without touching anything else. */
  setCatalogue: async (id: number, isCatalogItem: boolean): Promise<{ id: number; isCatalogItem: boolean }> => {
    const response = await axiosInstance.post<{ id: number; isCatalogItem: boolean }>(`/api/Product/${id}/catalogue`, { isCatalogItem });
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

  getWarehouses: async (): Promise<ProductWarehouseLookup[]> => {
    const r = await axiosInstance.get<ProductWarehouseLookup[]>('/api/Product/lookups/warehouses');
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

  getCurrencies: async (): Promise<ProductCurrencyLookup[]> => {
    const r = await axiosInstance.get('/api/Product/lookups/currencies');
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

  matchProduct: async (query: { name?: string; description?: string; partNo?: string; manufacturer?: string; businessUnitId?: number; quantity?: number }) => {
    const response = await axiosInstance.post<{
      hasExactMatch: boolean;
      exactMatch: ProductMatchSuggestion | null;
      fuzzyMatches: ProductMatchSuggestion[];
    }>('/api/Product/match-product', query);
    return response.data;
  },
};

export default productService;
