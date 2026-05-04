import axiosInstance from '../axiosInstance';

export interface SupplierPurchaseHistoryItem {
  id: number;
  productId: number;
  productName?: string;
  partNo?: string;
  supplierId: number;
  supplierName?: string;
  purchaseDate: string;
  quantity: number;
  unitPrice: number;
  currency?: string;
  batchNo?: string;
  expiryDate?: string;
  poDocId?: string;
  createdBy: string;
  createdOn: string;
}

const supplierPurchaseHistoryService = {
  getAll: async (businessUnitId: number) => {
    const response = await axiosInstance.get<SupplierPurchaseHistoryItem[]>(
      '/api/SupplierPurchaseHistory',
      { params: { businessUnitId } }
    );
    return response.data;
  },

  createBatch: async (data: { items: any[]; businessUnitId: number }) => {
    const response = await axiosInstance.post('/api/SupplierPurchaseHistory/batch', data);
    return response.data;
  },

  deleteByPoNumber: async (poDocId: string, businessUnitId: number) => {
    const response = await axiosInstance.delete(`/api/SupplierPurchaseHistory/po/${poDocId}`, {
      params: { businessUnitId },
    });
    return response.data;
  },
};

export default supplierPurchaseHistoryService;
