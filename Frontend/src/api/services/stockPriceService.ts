import axiosInstance from '../axiosInstance';

/** One earlier price for this part. SOLD = an order line, WON = a quote the customer accepted, QUOTED = a sent quote. */
export interface PriceReference {
  kind: 'SOLD' | 'WON' | 'QUOTED';
  unitPrice: number;
  currencyCode?: string | null;
  quantity: number;
  customer?: string | null;
  on?: string | null;
  reference: string;
}

export interface StockLinePrice {
  rfqItemId: number;
  productId?: number | null;
  partNumber?: string | null;
  description?: string | null;
  maker?: string | null;
  requestedQuantity: number;
  unit?: string | null;
  stock: {
    onHand: number;
    free: number;
    heldForOrders: number;
    places: { warehouse: string; onHand: number; free: number }[];
  };
  price: {
    source: 'SELLING_PRICE' | 'SUPPLIER_PLUS_MARGIN' | 'COST_PLUS_MARGIN' | 'COST_ONLY' | 'NONE';
    sellingPrice?: number | null;
    unitCost?: number | null;
    marginPercent?: number | null;
    unitPrice?: number | null;
  };
  /** The company's own record on this part: last quoted (to anyone), last won, or never. */
  trackRecord: { lastQuoted?: PriceReference | null; lastWon?: PriceReference | null; timesQuoted: number; timesWon: number };
  /** Other recent prices, not repeating the two above. */
  history: PriceReference[];
  /** DRAFT: price changes here. SENT: a new price makes a draft revision. DECIDED: the customer already decided; final. */
  onQuote?: { quoteId: number; quoteNo: string; unitPrice: number; exStock: boolean; currencyCode?: string | null; state: 'DRAFT' | 'SENT' | 'DECIDED'; leadTimeDays?: number | null } | null;
  currency?: { id: number; code: string } | null;
  /** Set when pricing another maker the customer accepts, from our own stock. */
  otherMaker?: OtherMakerStock | null;
  /** Prices suppliers gave for this part, valid first and cheapest first. */
  supplierPrices?: SupplierPriceOption[];
  /** True when free stock covers the whole quantity: ex stock. Otherwise the line is priced with a delivery time. */
  coveredByStock?: boolean;
}

export interface SupplierPriceOption {
  id: number;
  supplierName: string;
  cost: number;
  currencyCode?: string | null;
  leadTimeDays?: number | null;
  validUntil?: string | null;
  valid: boolean;
  reference?: string | null;
  forThisRequest: boolean;
  quotedOn?: string | null;
}

/** Another maker the customer accepts for the line, found in the catalogue with stock. */
export interface OtherMakerStock {
  productId: number;
  partNumber: string;
  label: string;
  onHand: number;
  free: number;
}

const stockPriceService = {
  get: async (rfqId: number, itemId: number, productId?: number | null): Promise<StockLinePrice> =>
    (await axiosInstance.get<StockLinePrice>(`/api/rfq/${rfqId}/items/${itemId}/stock-price`, { params: productId ? { productId } : undefined })).data,

  otherMakers: async (rfqId: number, itemId: number): Promise<OtherMakerStock[]> =>
    (await axiosInstance.get<OtherMakerStock[]>(`/api/rfq/${rfqId}/items/${itemId}/other-makers-in-stock`)).data,

  use: async (rfqId: number, itemId: number, body: { unitPrice: number; exStock: boolean; currencyId?: number | null; reviseIfSent?: boolean; productId?: number | null; leadTimeDays?: number | null }) =>
    (await axiosInstance.post<{ quoteId: number; quoteNo: string }>(`/api/rfq/${rfqId}/items/${itemId}/stock-price`, body)).data,

  getMargin: async () =>
    (await axiosInstance.get<{ marginPercent: number | null }>('/api/rfq/stock-margin')).data,

  saveMargin: async (marginPercent: number | null) =>
    (await axiosInstance.put<{ marginPercent: number | null }>('/api/rfq/stock-margin', { marginPercent })).data,
};

export default stockPriceService;
