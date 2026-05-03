import axiosInstance from '../axiosInstance';

export interface OrderDTO {
  id: number;
  orderNumber: string;
  customerId: number;
  customerName?: string;
  orderDate: string;
  totalAmount: number;
  status: string;
  businessUnitId: number;
  // Add other fields as needed based on OrderDto.cs
}

export interface OrderStatsDTO {
  totalOrders: number;
  pendingOrders: number;
  completedOrders: number;
  cancelledOrders: number;
}

const orderService = {
  getAll: async (businessUnitId: number) => {
    const response = await axiosInstance.get<OrderDTO[]>(`/api/Order`, { params: { businessUnitId } });
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
