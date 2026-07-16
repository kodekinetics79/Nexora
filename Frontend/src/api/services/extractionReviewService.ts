import axiosInstance from '../axiosInstance';
import leadService from './leadService';
import type { LeadResponseDTO, PaginatedResponse } from './leadService';

// A single row in the "needs review" queue. Mirrors the backend contract for
// GET /api/Lead/needs-review — items whose AI extraction requires a human to
// verify/correct before they can flow downstream.
export interface NeedsReviewItem {
  id: number;
  rfqno: string | null;
  buyersName: string | null;
  recDate: string;
  bidClosingDate: string | null;
  leadSource: string;
  aiconfidence: number | null;
  itemCount: number;
  reviewReason: string | null;
  receivedOn: string | null;
}

export interface NeedsReviewParams {
  pageNumber?: number;
  pageSize?: number;
  search?: string;
}

// Editable header fields the reviewer can correct before saving/approving.
export interface ReviewHeaderPayload {
  rfqno?: string;
  buyersName?: string;
  bidClosingDate?: string;
  opportunityNo?: string;
  headerRemarks?: string;
}

// Editable line-item fields. `id` is present for existing rows and omitted for
// rows the reviewer adds during review.
export interface ReviewItemPayload {
  id?: number;
  lineItemNo?: string;
  productShortName?: string;
  productShortDescription?: string;
  commodityProduct?: string;
  itemMaterialCode?: string;
  currency?: string;
  unitOfMeasure?: string;
  unitPrice?: number;
  quantity?: number;
  manufacturerName?: string;
  manufacturerPartNumber?: string;
  alternateProductName?: string;
  alternatePartNumber?: string;
  itemText?: string;
  leadTime?: string;
}

export interface SubmitReviewPayload {
  action: 'save' | 'approve';
  header: ReviewHeaderPayload;
  items: ReviewItemPayload[];
}

const extractionReviewService = {
  // Server-side paginated queue of documents awaiting human review.
  getNeedsReview: async (params: NeedsReviewParams): Promise<PaginatedResponse<NeedsReviewItem>> => {
    const r = await axiosInstance.get('/api/Lead/needs-review', { params });
    return r.data;
  },

  // The full lead used to seed the review workbench. Reuses the existing typed
  // leadService.getById so we keep one source of truth for GET /api/Lead/{id}.
  getLead: (id: number): Promise<LeadResponseDTO> => leadService.getById(id),

  // Persist reviewer corrections. `action: 'save'` keeps it in the queue as a
  // draft; `action: 'approve'` clears the review flag and returns the updated
  // lead.
  submitReview: async (id: number, payload: SubmitReviewPayload): Promise<LeadResponseDTO> => {
    const r = await axiosInstance.put(`/api/Lead/${id}/review`, payload);
    return r.data;
  },
};

export default extractionReviewService;
