import axiosInstance from '../axiosInstance';

export interface CustomerDTO {
  id: number;
  docId?: string;
  name: string;
  contactEmail?: string;
  imageUrl?: string;
  billingAddressLine1?: string;
  billingAddressLine2?: string;
  billingCity?: string;
  billingState?: string;
  billingCountry?: string;
  billingPostalCode?: string;
  shippingAddressLine1?: string;
  shippingAddressLine2?: string;
  shippingCity?: string;
  shippingState?: string;
  shippingCountry?: string;
  shippingPostalCode?: string;
  buid?: number;
  businessUnitName?: string;
  isActive?: boolean;
  createdBy?: string;
  createdOn?: string;
  modifiedBy?: string;
  modifiedOn?: string;
  concurrencyToken: string;

  // ── FR-CST-01 customer master ──────────────────────────────────────────────
  // Every one of these is optional on the wire and `undefined`/`null` means NOT CAPTURED.
  // The screens render a stated gap for each ("Not captured", "Not classified",
  // "No account team") rather than an empty cell — a blank in a register reads as a loading
  // state, and a register that looks complete when it is not is why this module was reported
  // as unmet in the first place.

  /** KSA commercial registration (10 digits), or a foreign registration with its country prefix. */
  commercialRegistrationNumber?: string | null;
  /** VAT registration number, validated server-side by the same rule as suppliers. */
  taxRegistrationNumber?: string | null;
  /** Stored CODE, not a label: 'GOVERNMENT' | 'SEMI_GOVERNMENT' | 'PRIVATE'. */
  sector?: string | null;
  /** Key into the tenant's governed region master (SetState), not a typed string. */
  regionStateId?: number | null;
  /** Resolved region name for display. A projection of regionStateId, never a substitute. */
  regionName?: string | null;
  /** FR-CST-02 — the account team that owns this customer. */
  accountTeamId?: number | null;
  accountTeamName?: string | null;

  /**
   * AA-01 · raw jsonb bag of tenant-defined custom field values, keyed by stable key.
   * Read through `readCustomFieldValue` — never parsed inline, because the column is open
   * jsonb and a malformed payload must render blank rather than break the grid.
   */
  customFields?: string | null;
}

export interface PaginatedCustomerResponse {
  items: CustomerDTO[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
}

const toServerAuthoritativeFormData = (data: FormData): FormData => {
  const result = new FormData();
  const prohibited = new Set(['createdby', 'modifiedby', 'buid', 'businessunitid']);
  data.forEach((value, key) => {
    if (!prohibited.has(key.toLowerCase())) result.append(key, value);
  });
  return result;
};

const customerService = {
  getAll: async (params: { pageNumber?: number; pageSize?: number; name?: string; isActive?: boolean }): Promise<PaginatedCustomerResponse> => {
    const r = await axiosInstance.get('/api/Customer', { params });
    return r.data;
  },

  getById: async (id: number): Promise<CustomerDTO> => {
    const r = await axiosInstance.get(`/api/Customer/${id}`);
    return r.data;
  },

  create: async (data: FormData): Promise<CustomerDTO> => {
    const r = await axiosInstance.post('/api/Customer', toServerAuthoritativeFormData(data), {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
    return r.data;
  },

  update: async (id: number, data: FormData): Promise<void> => {
    await axiosInstance.put(`/api/Customer/${id}`, toServerAuthoritativeFormData(data), {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
  },

  delete: async (id: number, concurrencyToken: string): Promise<void> => {
    await axiosInstance.delete(`/api/Customer/${id}`, { params: { concurrencyToken } });
  },

  getCustomerByEmail: async (email: string): Promise<CustomerDTO | null> => {
    const r = await axiosInstance.get('/api/Customer/by-email', { params: { email } });
    return r.data;
  },

  // Upload / Export
  downloadTemplate: () =>
    axiosInstance.get('/api/CustomerUploader/download-template', { responseType: 'blob' }),

  uploadTemplate: (file: File) => {
    const fd = new FormData();
    fd.append('file', file);
    return axiosInstance.post('/api/CustomerUploader/upload-template', fd, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
  },

  export: () =>
    axiosInstance.get('/api/CustomerUploader/export', { responseType: 'blob' }),
};

export default customerService;
