import axiosInstance from '../axiosInstance';

/** One part on the Pricing sheet: its landed cost and sale price, in the currency its keeper chose. */
export interface PricingRow {
  productId: number;
  partNo: string;
  name?: string | null;
  unit?: string | null;
  currencyId?: number | null;
  currencyCode?: string | null;
  landedCost?: number | null;
  salePrice?: number | null;
  /** The last supplier purchase price on record, for reference only. */
  lastPurchasePrice?: number | null;
  lastPurchaseCurrencyCode?: string | null;
  lastPurchaseOn?: string | null;
  onHand: number;
  changedOn?: string | null;
  changedBy?: string | null;
}

export interface PricingCurrency { id: number; code: string; isBase: boolean }

export interface PricingPage {
  rows: PricingRow[];
  total: number;
  page: number;
  pageSize: number;
  /** Parts with no landed cost, no sale price or no currency, across the whole sheet. */
  missingCount: number;
  currencies: PricingCurrency[];
}

export interface PriceChange { productId: number; landedCost: number | null; salePrice: number | null; currencyId: number | null }

const pricingSheetService = {
  get: async (params: { search?: string; missingOnly?: boolean; page?: number; pageSize?: number }): Promise<PricingPage> =>
    (await axiosInstance.get('/api/pricing-sheet', { params })).data,
  save: async (rows: PriceChange[]): Promise<{ saved: number }> =>
    (await axiosInstance.put('/api/pricing-sheet', { rows })).data,
};

export default pricingSheetService;
