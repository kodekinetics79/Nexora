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
    source: 'SELLING_PRICE' | 'COST_PLUS_MARGIN' | 'COST_ONLY' | 'NONE';
    sellingPrice?: number | null;
    unitCost?: number | null;
    marginPercent?: number | null;
    unitPrice?: number | null;
  };
  /** The company's own record on this part: last quoted (to anyone), last won, or never. */
  trackRecord: { lastQuoted?: PriceReference | null; lastWon?: PriceReference | null; timesQuoted: number; timesWon: number };
  /** Other recent prices, not repeating the two above. */
  history: PriceReference[];
  onQuote?: { quoteId: number; quoteNo: string; unitPrice: number; exStock: boolean; currencyCode?: string | null } | null;
  currency?: { id: number; code: string } | null;
}

const stockPriceService = {
  get: async (rfqId: number, itemId: number): Promise<StockLinePrice> =>
    (await axiosInstance.get<StockLinePrice>(`/api/rfq/${rfqId}/items/${itemId}/stock-price`)).data,

  use: async (rfqId: number, itemId: number, body: { unitPrice: number; exStock: boolean; currencyId?: number | null }) =>
    (await axiosInstance.post<{ quoteId: number; quoteNo: string }>(`/api/rfq/${rfqId}/items/${itemId}/stock-price`, body)).data,

  getMargin: async () =>
    (await axiosInstance.get<{ marginPercent: number | null }>('/api/rfq/stock-margin')).data,

  saveMargin: async (marginPercent: number | null) =>
    (await axiosInstance.put<{ marginPercent: number | null }>('/api/rfq/stock-margin', { marginPercent })).data,
};

export default stockPriceService;
