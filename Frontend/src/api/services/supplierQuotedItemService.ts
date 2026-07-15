import axiosInstance from '../axiosInstance';

export interface SupplierQuotedItemDTO {
  id: number;
  supplierId: number;
  supplierName?: string;
  itemName: string;
  description: string;
  uomId: number;
  uomName?: string;
  quantity: number;
  unitPrice: number;
  currencyId: number;
  currencyName?: string;
  quoteReference: string;
  quoteDate: string;
  validUntil: string;
  taxAmount: number;
  discountAmount: number;
  isActive: boolean;
  businessUnitId: number;
}

const supplierQuotedItemService = {
  getAll: async (businessUnitId: number) => {
    const response = await axiosInstance.get<SupplierQuotedItemDTO[]>(`/api/SupplierQuotedItem`, { params: { businessUnitId } });
    return response.data;
  },

  getById: async (id: number, businessUnitId: number) => {
    const response = await axiosInstance.get<SupplierQuotedItemDTO>(`/api/SupplierQuotedItem/${id}`, { params: { businessUnitId } });
    return response.data;
  },

  getBySupplier: async (supplierId: number, businessUnitId: number) => {
    const response = await axiosInstance.get<SupplierQuotedItemDTO[]>(`/api/SupplierQuotedItem/GetBySupplier/${supplierId}`, { params: { businessUnitId } });
    return response.data;
  },

  create: async (data: any) => {
    const response = await axiosInstance.post<SupplierQuotedItemDTO>(`/api/SupplierQuotedItem`, data);
    return response.data;
  },

  update: async (id: number, data: any) => {
    const response = await axiosInstance.put(`/api/SupplierQuotedItem/${id}`, data);
    return response.data;
  },

  delete: async (id: number, businessUnitId: number) => {
    const response = await axiosInstance.delete(`/api/SupplierQuotedItem/${id}`, { params: { businessUnitId } });
    return response.data;
  }
};

export default supplierQuotedItemService;
