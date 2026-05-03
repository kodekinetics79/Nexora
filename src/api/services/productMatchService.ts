import axiosInstance from '../axiosInstance';

export interface ProductMatchRequest {
  partNumber?: string;
  manufacturer?: string;
  description?: string;
  businessUnitId: number;
}

export interface ProductMatchSuggestion {
  productId: number;
  productName: string;
  partNo: string;
  manufacturer?: string;
  description?: string;
  qtyOnHand: number;
  unitCost?: number;
  sellingPrice?: number;
  leadTime?: number;
  preferredSupplierId?: number;
  preferredSupplierName?: string;
  preferredSupplierEmail?: string;
  matchConfidence: number;
  matchReason: string;
}

export interface ProductMatchResponse {
  hasExactMatch: boolean;
  exactMatch?: ProductMatchSuggestion;
  fuzzyMatches: ProductMatchSuggestion[];
}

export interface StockDetails {
  productId: number;
  productName: string;
  partNo: string;
  qtyOnHand: number;
  reorderPoint: number;
  warehouseName?: string;
  stockPartNumber?: string;
  unitCost?: number;
  sellingPrice?: number;
  replacementCost?: number;
  currency?: string;
  leadTime?: number;
  hasPurchaseHistory: boolean;
}

export interface PurchaseHistoryItem {
  orderId: number;
  orderNumber?: string;
  orderDate: string;
  supplierName?: string;
  quantity: number;
  unitPrice: number;
  currency?: string;
}

export interface PurchaseHistory {
  productId: number;
  purchaseHistory: PurchaseHistoryItem[];
}

const productMatchService = {
  matchProduct: async (request: ProductMatchRequest) => {
    const response = await axiosInstance.post<ProductMatchResponse>('/api/Product/match-product', request);
    return response.data;
  },

  getStockDetails: async (productId: number, businessUnitId: number) => {
    const response = await axiosInstance.get<StockDetails>(`/api/Product/${productId}/stock-details`, {
      params: { businessUnitId }
    });
    return response.data;
  },

  getPurchaseHistory: async (productId: number, businessUnitId: number) => {
    const response = await axiosInstance.get<PurchaseHistory>(`/api/Product/${productId}/purchase-history`, {
      params: { businessUnitId }
    });
    return response.data;
  }
};

export default productMatchService;
