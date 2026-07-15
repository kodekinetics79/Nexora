import axiosInstance from '../axiosInstance';

export interface ShipmentItemDTO {
  id?: number;
  orderItemId: number;
  productName?: string;
  quantity: number;
  notes?: string;
}

export interface ShipmentStatusHistoryDTO {
  id: number;
  previousStatusId?: number;
  previousStatus?: string;
  newStatusId: number;
  newStatus: string;
  changedBy: string;
  changedOn: string;
  notes?: string;
}

export interface ShipmentDTO {
  id: number;
  shipmentNo: string;
  orderId: number;
  orderNo: string;
  statusId: number;
  status: string;
  shipmentDate: string;
  estimatedDeliveryDate?: string;
  actualDeliveryDate?: string;
  carrier?: string;
  serviceLevel?: string;
  trackingNumber?: string;
  externalId?: string;
  shippingCost?: number;
  labelUrl?: string;
  shippingAddress?: string;
  notes?: string;
  items: ShipmentItemDTO[];
  statusHistory?: ShipmentStatusHistoryDTO[];
}

export interface CreateShipmentDTO {
  orderId: number;
  businessUnitId: number;
  statusId: number;
  shipmentDate: string;
  estimatedDeliveryDate?: string;
  carrier?: string;
  serviceLevel?: string;
  trackingNumber?: string;
  externalId?: string;
  shippingCost?: number;
  labelUrl?: string;
  shippingAddress?: string;
  notes?: string;
  items: ShipmentItemDTO[];
}

const shipmentService = {
  getAll: async (params: { businessUnitId?: number }) => {
    const response = await axiosInstance.get<ShipmentDTO[]>('/api/Shipment', { params });
    return response.data;
  },

  getById: async (id: number, businessUnitId?: number) => {
    const response = await axiosInstance.get<ShipmentDTO>(`/api/Shipment/${id}`, { params: { businessUnitId } });
    return response.data;
  },

  getByOrderId: async (orderId: number, businessUnitId?: number) => {
    const response = await axiosInstance.get<ShipmentDTO[]>(`/api/Shipment/order/${orderId}`, { params: { businessUnitId } });
    return response.data;
  },

  create: async (data: CreateShipmentDTO) => {
    const response = await axiosInstance.post<ShipmentDTO>('/api/Shipment', data);
    return response.data;
  },

  update: async (id: number, data: Partial<ShipmentDTO>) => {
    const response = await axiosInstance.put<ShipmentDTO>(`/api/Shipment/${id}`, data);
    return response.data;
  },

  delete: async (id: number, businessUnitId?: number) => {
    await axiosInstance.delete(`/api/Shipment/${id}`, { params: { businessUnitId } });
  }
};

export default shipmentService;
