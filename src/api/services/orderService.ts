import axiosInstance from '../axiosInstance';

export interface OrderDTO {
  id: number;
  orderNo: string;
  orderNumber?: string; // Legacy support
  customerId: number;
  customerName: string;
  quoteId?: number;
  quoteNo?: string;
  rfqId?: number;
  rfqNo?: string;
  leadId?: number;
  leadNo?: string;
  statusId: number;
  status: string;
  paymentStatusId?: number;
  paymentStatus: string;
  paymentMethodId?: number;
  totalAmount: number;
  subTotal: number;
  taxAmount: number;
  discountAmount: number;
  paidAmount: number;
  balanceAmount: number;
  orderDate: string;
  deliveryDate?: string;
  paymentReference?: string;
  notes?: string;
  termsAndConditions?: string;
  hasShipments: boolean;
  businessUnitId: number;
  items: OrderItemDTO[];
}

export interface OrderItemDTO {
  id: number;
  productId: number;
  productName: string;
  description?: string;
  quantity: number;
  unitPrice: number;
  discount: number;
  taxAmount: number;
  totalAmount: number;
  uomId?: number;
  warehouseId?: number;
}

export interface OrderStatsDTO {
  totalOrders: number;
  pendingOrders: number;
  completedOrders: number;
  cancelledOrders: number;
}

const orderService = {
  getAll: async (params: { businessUnitId: number; pageNumber?: number; pageSize?: number; search?: string }) => {
    const response = await axiosInstance.get<OrderDTO[]>(`/api/Order`, { params });
    return response.data;
  },

  getById: async (id: number, businessUnitId: number) => {
    const response = await axiosInstance.get<OrderDTO>(`/api/Order/${id}`, { params: { businessUnitId } });
    return response.data;
  },

  createManual: async (data: any) => {
    const response = await axiosInstance.post<OrderDTO>(`/api/Order`, data);
    return response.data;
  },

  createFromRfq: async (rfqId: number, businessUnitId: number) => {
    const response = await axiosInstance.post<OrderDTO>(`/api/Order/from-rfq/${rfqId}`, null, { params: { businessUnitId } });
    return response.data;
  },

  createFromQuote: async (quoteId: number, businessUnitId: number) => {
    const response = await axiosInstance.post<OrderDTO>(`/api/Order/from-quote/${quoteId}`, null, { params: { businessUnitId } });
    return response.data;
  },

  update: async (id: number, data: any) => {
    const response = await axiosInstance.put<OrderDTO>(`/api/Order/${id}`, data);
    return response.data;
  },

  delete: async (id: number, businessUnitId: number) => {
    const response = await axiosInstance.delete(`/api/Order/${id}`, { params: { businessUnitId } });
    return response.data;
  },

  getByCustomer: async (customerId: number, businessUnitId: number) => {
    const response = await axiosInstance.get<OrderDTO[]>(`/api/Order/customer/${customerId}`, { params: { businessUnitId } });
    return response.data;
  },

  getInvoice: async (id: number, businessUnitId: number) => {
    const response = await axiosInstance.get(`/api/Order/${id}/invoice`, { params: { businessUnitId } });
    return response.data;
  },

  getStats: async () => {
    const response = await axiosInstance.get<OrderStatsDTO>(`/api/Order/stats`);
    return response.data;
  }
};

export default orderService;
