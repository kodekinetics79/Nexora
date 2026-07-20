import axiosInstance from '../axiosInstance';

// ─── Service RFQ → BOQ (Bill of Quantities) ──────────────────────────────────
// Mirrors Backend Boq/BoqDtos.cs wire contracts. Raw model scores (0..1) are
// never shown to users — the pages map them to High / Medium / Low.

export type BoqStatus = 'Draft' | 'InReview' | 'Approved';
export type BoqItemType = 'Material' | 'Labor' | 'Equipment' | 'Subcontract';
export type BoqItemSource = 'extracted' | 'assembly' | 'manual';

export const BOQ_SERVICE_CATEGORIES = [
  'electrical',
  'mechanical',
  'civil',
  'maintenance',
  'manpower',
  'mixed',
  'other',
] as const;
export type BoqServiceCategory = (typeof BOQ_SERVICE_CATEGORIES)[number];

export interface BoqItemDto {
  id: number;
  seq: number;
  itemCode: string | null;
  description: string;
  unit: string;
  quantity: number;
  itemType: BoqItemType;
  unitRate: number | null;
  totalAmount: number | null;
  source: BoqItemSource;
  /** 0..1 — never shown raw in the UI. */
  confidence: number | null;
  /** True when this line still needs human details (e.g. a quantity). */
  isTbd: boolean;
  assemblyCode: string | null;
  /** True when the tenant library can explode this line into components. */
  canExplode: boolean;
  evidenceNote: string | null;
}

export interface BoqSectionDto {
  id: number;
  seq: number;
  title: string;
  totalAmount: number;
  items: BoqItemDto[];
}

export interface BoqDocumentDto {
  id: number;
  leadId: number | null;
  title: string;
  serviceCategory: BoqServiceCategory;
  status: BoqStatus;
  /** 0..1 — never shown raw in the UI. */
  overallConfidence: number | null;
  notes: string | null;
  assumptions: string[];
  totalAmount: number;
  tbdCount: number;
  itemCount: number;
  createdBy: string | null;
  createdOn: string;
  updatedOn: string;
  approvedBy: string | null;
  approvedOn: string | null;
  sections: BoqSectionDto[];
}

export interface BoqListItemDto {
  id: number;
  leadId: number | null;
  title: string;
  serviceCategory: BoqServiceCategory;
  status: BoqStatus;
  overallConfidence: number | null;
  totalAmount: number;
  tbdCount: number;
  itemCount: number;
  createdOn: string;
  updatedOn: string;
}

export interface BoqListResultDto {
  items: BoqListItemDto[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface BoqAssemblyComponentDto {
  id: number;
  seq: number;
  description: string;
  unit: string;
  qtyPer: number;
  itemType: BoqItemType;
  defaultRate: number | null;
}

export interface BoqAssemblyDto {
  id: number;
  code: string;
  name: string;
  description: string | null;
  serviceCategory: string;
  unit: string;
  isStarter: boolean;
  components: BoqAssemblyComponentDto[];
}

export interface BoqDraftRequest {
  leadId?: number;
  title?: string;
  text?: string;
  serviceCategory?: string;
  fileName?: string;
  mimeType?: string;
}

export interface BoqItemUpdate {
  id?: number | null;
  seq: number;
  itemCode?: string | null;
  description: string;
  unit: string;
  quantity: number;
  itemType: BoqItemType;
  unitRate?: number | null;
  isTbd: boolean;
  assemblyCode?: string | null;
  evidenceNote?: string | null;
}

export interface BoqSectionUpdate {
  id?: number | null;
  seq: number;
  title: string;
  items: BoqItemUpdate[];
}

export interface BoqUpdateRequest {
  header?: {
    title?: string;
    serviceCategory?: string;
    notes?: string;
    status?: 'Draft' | 'InReview';
    assumptions?: string[];
  };
  sections?: BoqSectionUpdate[];
}

const boqService = {
  draft: async (body: BoqDraftRequest): Promise<BoqDocumentDto> => {
    const r = await axiosInstance.post<BoqDocumentDto>('/api/boq/draft', body);
    return r.data;
  },

  list: async (params: {
    page?: number;
    pageSize?: number;
    status?: string;
    search?: string;
  }): Promise<BoqListResultDto> => {
    const r = await axiosInstance.get<BoqListResultDto>('/api/boq', { params });
    return r.data;
  },

  get: async (id: number): Promise<BoqDocumentDto> => {
    const r = await axiosInstance.get<BoqDocumentDto>(`/api/boq/${id}`);
    return r.data;
  },

  update: async (id: number, body: BoqUpdateRequest): Promise<BoqDocumentDto> => {
    const r = await axiosInstance.put<BoqDocumentDto>(`/api/boq/${id}`, body);
    return r.data;
  },

  approve: async (id: number): Promise<BoqDocumentDto> => {
    const r = await axiosInstance.post<BoqDocumentDto>(`/api/boq/${id}/approve`);
    return r.data;
  },

  assemblies: async (): Promise<BoqAssemblyDto[]> => {
    const r = await axiosInstance.get<BoqAssemblyDto[]>('/api/boq/assemblies');
    return r.data;
  },

  explodeItem: async (itemId: number, code?: string): Promise<BoqDocumentDto> => {
    const r = await axiosInstance.post<BoqDocumentDto>(
      `/api/boq/items/${itemId}/explode`,
      undefined,
      { params: code ? { code } : undefined }
    );
    return r.data;
  },

  /** Downloads the CSV export and triggers a browser save. */
  exportCsv: async (id: number, title?: string): Promise<void> => {
    const r = await axiosInstance.get(`/api/boq/${id}/export.csv`, { responseType: 'blob' });
    const url = window.URL.createObjectURL(new Blob([r.data], { type: 'text/csv' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = `${(title || `boq-${id}`).replace(/[^\w.-]+/g, '-')}.csv`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.URL.revokeObjectURL(url);
  },
};

export default boqService;
