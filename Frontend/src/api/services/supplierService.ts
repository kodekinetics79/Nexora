import axiosInstance from '../axiosInstance';

/**
 * FR-MDM-03 · the customer-set supplier tier.
 *
 * Tier is master data, not a compliance verdict. It annotates, orders and pre-selects suppliers for
 * dispatch; it never gates one. A brand-new compliant supplier is legitimately Tier 3 and a Tier 1
 * partner with a lapsed registration is legitimately blocked — governance status and tier are
 * different axes and the UI must keep them apart. Nothing derives a tier: no spend band, no
 * auto-promotion, no sync with governance.
 */
export const SUPPLIER_TIERS = [
  { value: 'TIER_1_PARTNER', label: 'Tier 1 — Partner' },
  { value: 'TIER_2_EXTENDED', label: 'Tier 2 — Extended network' },
  { value: 'TIER_3_OUT_OF_NETWORK', label: 'Tier 3 — Out of network' },
] as const;

/** Null/blank tier is a real state — "not classified" — and is never displayed as a tier. */
export const supplierTierLabel = (tier?: string | null): string =>
  SUPPLIER_TIERS.find((option) => option.value === tier)?.label ?? 'Not classified';

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
  /**
   * VAT/tax registration number. Optional, but a supplier without one cannot have the input tax
   * it charges treated as recoverable — the reclaim has to name the supplier to ZATCA — so
   * capturing supplier quotes from it will be refused while the tenant's recoverable percentage
   * is above zero.
   */
  taxRegistrationNumber?: string;
  /** One of SUPPLIER_TIERS, or absent meaning not yet classified. */
  tier?: string;
  /**
   * The numeric companion to the free-text `paymentTerms`. Absent means NOT CONFIGURED — it is not
   * zero, and it is not "pay immediately". A person types this number; nothing infers it.
   */
  creditDays?: number;
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

  /**
   * Candidates for a supplier RFQ.
   *
   * `tiers` is a narrowing hint, not the filter the buyer sees. The screen filters the candidates
   * it gets back as well, so which suppliers are listed is decided in one place whether or not the
   * server has learned to narrow the query yet. A hint the server does not recognise is simply
   * ignored by it, which is why it can never hide a supplier on its own.
   */
  searchSuppliers: async (
    searchTerm: string,
    productCategory: string,
    businessUnitId: number,
    tiers?: string[],
  ): Promise<SupplierDTO[]> => {
    const response = await axiosInstance.get<SupplierDTO[]>("/api/Supplier/search", {
      params: {
        searchTerm,
        productCategory,
        businessUnitId,
        ...(tiers && tiers.length > 0 ? { tiers } : {}),
      }
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
