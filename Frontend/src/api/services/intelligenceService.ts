import axiosInstance from '../axiosInstance';

// ─── Lead → RFQ conversion preview ───────────────────────────────────────────

export interface ProductMatchSuggestion {
  productId: number;
  productName: string | null;
  materialCode: string | null;
  manufacturerPartNumber: string | null;
  /** 0..1 — never shown raw in the UI; mapped to High / Medium / Low. */
  score: number | null;
  /** Plain-language explanation of why this product was suggested. */
  reason: string | null;
}

export interface ConversionPreviewHeader {
  rfqno: string | null;
  buyersName: string | null;
  recDate: string | null;
  bidClosingDate: string | null;
}

export interface ConversionPreviewItem {
  leadItemId: number;
  /** The raw text this line was extracted from. */
  sourceText: string | null;
  quantity: number | null;
  unitOfMeasure: string | null;
  normalizedQuantity: number | null;
  normalizedUom: string | null;
  matches: ProductMatchSuggestion[];
  bestMatchProductId: number | null;
  /** 0..1 — never shown raw in the UI. */
  confidence: number | null;
  needsAttention: boolean;
  attentionReason: string | null;
}

export interface ConversionPreview {
  leadId: number;
  header: ConversionPreviewHeader;
  items: ConversionPreviewItem[];
  /** 0..1 — never shown raw in the UI. */
  overallConfidence: number | null;
}

export interface ConvertLeadItemRequest {
  leadItemId: number;
  include: boolean;
  productId?: number;
  quantity?: number;
  unitOfMeasure?: string;
}

export interface ConvertLeadRequest {
  items: ConvertLeadItemRequest[];
  notes?: string;
}

export interface ConvertLeadResponse {
  rfqId: number;
}

// ─── RFQ smart-pricing preview ───────────────────────────────────────────────

export type PriceSignalSource =
  | 'priceList'
  | 'recentQuote'
  | 'supplierQuote'
  | 'purchaseHistory'
  | 'productMaster';

export interface PriceSignal {
  source: PriceSignalSource;
  label: string | null;
  value: number | null;
  detail: string | null;
}

export interface PricePreviewLine {
  rfqItemId: number;
  description: string | null;
  quantity: number | null;
  recommendedUnitPrice: number | null;
  floorUnitPrice: number | null;
  marginPct: number | null;
  /** 0..1 — never shown raw in the UI. */
  confidence: number | null;
  /** Plain-language sentence explaining the recommendation. */
  rationale: string | null;
  signals: PriceSignal[];
  needsAttention: boolean;
}

export interface PricePreviewTotals {
  recommendedTotal: number | null;
}

export interface PricePreview {
  rfqId: number;
  currency: string | null;
  lines: PricePreviewLine[];
  totals: PricePreviewTotals;
  /** 0..1 — never shown raw in the UI. */
  overallConfidence: number | null;
}

export interface ApplyPricingLineRequest {
  rfqItemId: number;
  unitPrice: number;
}

export interface ApplyPricingRequest {
  lines: ApplyPricingLineRequest[];
}

export interface ApplyPricingResponse {
  applied: number;
  total: number | null;
}

// ─── GET /api/intelligence/customers/{id}/context (WP-B2) ────────────────────
// Mirrors Backend Controllers/CustomerContextController.cs wire contracts.

export interface CustomerKeyLineDTO {
  description: string | null;
  quantity: number;
  unitPrice: number;
}

export interface CustomerQuoteSummaryDTO {
  quoteId: number;
  quoteNo: string;
  quoteDate: string | null;
  totalAmount: number | null;
  statusValue: string | null;
  /** "won" | "lost" | "open" */
  outcome: 'won' | 'lost' | 'open';
  outcomeReasonName: string | null;
  keyLines: CustomerKeyLineDTO[];
}

export interface CustomerItemPriceDTO {
  productId: number | null;
  description: string | null;
  unitPrice: number;
  quoteDate: string | null;
  monthsAgo: number | null;
}

export interface CustomerRfqSummaryDTO {
  rfqId: number;
  rfqNo: string;
  receivedOn: string;
  bidClosingOn: string | null;
  status: string | null;
  lineCount: number;
}

export interface CustomerOrderSummaryDTO {
  orderId: number;
  orderNo: string;
  orderDate: string;
  status: string | null;
  totalAmount: number;
  quoteId: number | null;
}

export interface CustomerDemandSummaryDTO {
  productId: number | null;
  partNumber: string | null;
  description: string | null;
  inquiryCount: number;
  requestedQuantity: number;
}

export interface CustomerContextDTO {
  customerId: number;
  customerName: string | null;
  totalQuotes: number;
  wonQuotes: number;
  lostQuotes: number;
  /** 0–100; null while nothing has been decided yet. */
  winRatePct: number | null;
  ordersLast24Months: number;
  orderValueLast24Months: number;
  avgQuoteTotal: number | null;
  /** Average margin era (0–100); null when no cost floor data exists. */
  avgMarginPct: number | null;
  lastQuoteDate: string | null;
  recentQuotes: CustomerQuoteSummaryDTO[];
  recentItemPrices: CustomerItemPriceDTO[];
  recentRfqs: CustomerRfqSummaryDTO[];
  recentOrders: CustomerOrderSummaryDTO[];
  demandProfile: CustomerDemandSummaryDTO[];
  generatedAt: string;
}

// ─── Service ─────────────────────────────────────────────────────────────────

const intelligenceService = {
  getConversionPreview: async (leadId: number): Promise<ConversionPreview> => {
    const r = await axiosInstance.get<ConversionPreview>(
      `/api/intelligence/leads/${leadId}/conversion-preview`
    );
    return r.data;
  },

  convertLead: async (leadId: number, body: ConvertLeadRequest): Promise<ConvertLeadResponse> => {
    const r = await axiosInstance.post<ConvertLeadResponse>(
      `/api/intelligence/leads/${leadId}/convert`,
      body
    );
    return r.data;
  },

  getPricePreview: async (rfqId: number): Promise<PricePreview> => {
    const r = await axiosInstance.get<PricePreview>(
      `/api/intelligence/rfqs/${rfqId}/price-preview`
    );
    return r.data;
  },

  applyPricing: async (rfqId: number, body: ApplyPricingRequest): Promise<ApplyPricingResponse> => {
    const r = await axiosInstance.post<ApplyPricingResponse>(
      `/api/intelligence/rfqs/${rfqId}/apply-pricing`,
      body
    );
    return r.data;
  },

  /** WP-B2: "This customer" history + last-sold prices, shown while quoting. */
  getCustomerContext: async (customerId: number): Promise<CustomerContextDTO> => {
    const r = await axiosInstance.get<CustomerContextDTO>(
      `/api/intelligence/customers/${customerId}/context`
    );
    return r.data;
  },
};

export default intelligenceService;
