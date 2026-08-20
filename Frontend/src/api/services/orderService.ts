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

  /**
   * The currency every monetary figure on this DTO is denominated in.
   *
   * `Order.CurrencyId` has always been persisted, the repository has always Included the
   * navigation, and `OrderDto` states both — this interface simply never declared them, so the
   * value arrived on the wire and no screen could read it. Orders, the order view and the shipment
   * order-summary panel all printed a literal "$" instead, on records that may be denominated in
   * SAR. Null means the order carries no currency; render it as a bare number, never as a house
   * default.
   */
  currencyId?: number | null;
  currencyCode?: string | null;

  /**
   * The commercial identity the order inherited from its quote. Emitted by `OrderDto` since the
   * case spine landed; undeclared here, so the Orders screens cannot show the serial a customer
   * quotes back at us.
   */
  commercialCaseId?: number | null;
  nexoraSerial?: string | null;
  contactId?: number | null;

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

// Mirrors Backend DTOs/OrderDTOs/OrderStatsDTO.cs (GET /api/Order/stats).
export interface OrderStatsCurrencyBreakdownDTO {
  currencyId: number | null;
  currencyCode: string | null;
  subtotal: number;
  orderCount: number;
  converted: boolean;
  exchangeRate: number | null;
  convertedSubtotal: number | null;
  reason: string | null;
}

export interface OrderStatsDTO {
  totalOrders: number;
  pendingOrders: number;
  completedOrders: number;
  /**
   * Converted into `totalRevenueCurrency` using approved FX rates. NULL means "not answerable",
   * never zero — do not coalesce it to 0. When it is null, `totalRevenueUnavailableReason`
   * says why and `revenueByCurrency` is the honest thing to show instead.
   */
  totalRevenue: number | null;
  totalRevenueCurrency: string | null;
  totalRevenueConverted: boolean;
  totalRevenueUnavailableReason: string | null;
  revenueByCurrency: OrderStatsCurrencyBreakdownDTO[];
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
