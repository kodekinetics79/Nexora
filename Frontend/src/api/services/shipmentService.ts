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
  /** ISO currency code inherited from the governed source order; absent only on legacy/unassigned orders. */
  currencyCode?: string;
  labelUrl?: string;
  shippingAddress?: string;
  /**
   * FR-DLM-05. The GOVERNED lifecycle: SCHEDULED, DISPATCHED, IN_TRANSIT, DELIVERED,
   * DELIVERY_EXCEPTION or CANCELLED. Distinct from `status`, which is the tenant's own
   * SetupMaster picklist label and is constrained by nothing.
   */
  deliveryStatus: string;
  deliveryStatusChangedOn?: string;
  deliveryStatusChangedBy?: string;
  /** FR-DLM-01. Null means the address is not mapped to a governed region, and screens say so. */
  deliveryCityId?: number;
  deliveryCityName?: string;
  deliveryRegionName?: string;
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
  /** FR-DLM-01. Governed region mapping for the delivery address, from the tenant's city master. */
  deliveryCityId?: number;
  notes?: string;
  /**
   * FR-MTR-02. The despatch supervisor's written acceptance of a lapsed certificate on material
   * going out on this note. The server refuses it when every lot being issued is in date, so it
   * is only ever sent when the operator has been shown a specific refusal asking for it.
   */
  complianceOverrideReason?: string;
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

  create: async (data: CreateShipmentDTO, idempotencyKey: string) => {
    const response = await axiosInstance.post<ShipmentDTO>('/api/Shipment', data, {
      headers: { 'Idempotency-Key': idempotencyKey },
    });
    return response.data;
  },

  update: async (id: number, data: Partial<ShipmentDTO>) => {
    const response = await axiosInstance.put<ShipmentDTO>(`/api/Shipment/${id}`, data);
    return response.data;
  },

  /**
   * Withdraws a shipment that never despatched.
   *
   * The reason is mandatory and the server rejects a blank one with 400. It is not optional here
   * either: the endpoint used to take no reason and no actor at all, and set `IsActive = false` on
   * any shipment including a signed one. The server refuses a despatched or proved shipment with
   * 409 and its own sentence — surface that verbatim rather than inventing copy for it.
   */
  delete: async (id: number, reason: string, businessUnitId?: number) => {
    await axiosInstance.delete(`/api/Shipment/${id}`, { params: { reason, businessUnitId } });
  }
};

export default shipmentService;
