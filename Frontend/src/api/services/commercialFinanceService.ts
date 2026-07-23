import axiosInstance from '../axiosInstance';

export interface ReceivableLine {
  id: number;
  orderItemId?: number;
  parentDocumentLineId?: number;
  description: string;
  quantity: number;
  unitPrice: number;
  discountAmount: number;
  taxAmount: number;
  lineTotal: number;
}

export interface ReceivableDocument {
  id: number;
  commercialCaseId?: number;
  customerId: number;
  orderId?: number;
  parentDocumentId?: number;
  adjustmentReasonCode?: string;
  adjustmentReason?: string;
  currencyId?: number;
  currencyCode?: string;
  documentType: string;
  status: string;
  documentNumber?: string;
  documentDate: string;
  dueDate: string;
  issuedOn?: string;
  voidedOn?: string;
  voidReason?: string;
  voidedBy?: string;
  subTotal: number;
  discountAmount: number;
  taxAmount: number;
  totalAmount: number;
  allocatedAmount: number;
  outstandingAmount: number;
  version: number;
  lines: ReceivableLine[];
}

export type ReceivableAdjustmentType = 'CreditNote' | 'DebitNote';

export interface CreateReceivableAdjustmentRequest {
  documentType: ReceivableAdjustmentType;
  documentDate: null;
  dueDate: null;
  reasonCode: string;
  reason: string;
  lines: { parentLineId: number; quantity: number }[];
}

export interface ArOpenItem {
  documentId: number;
  documentNumber: string;
  documentType: string;
  customerId: number;
  commercialCaseId?: number;
  currencyId?: number;
  currencyCode?: string;
  documentDate: string;
  dueDate: string;
  originalAmount: number;
  outstandingAmount: number;
  daysPastDue: number;
  agingBucket: string;
}

export interface CustomerPayment {
  id: number;
  customerId: number;
  commercialCaseId?: number;
  currencyId?: number;
  currencyCode?: string;
  receiptNumber: string;
  status: string;
  paymentDate: string;
  amount: number;
  allocatedAmount: number;
  unappliedAmount: number;
  version: number;
}

const withKey = (key: string) => ({ headers: { 'Idempotency-Key': key } });

const commercialFinanceService = {
  getDocuments: async (params?: { customerId?: number; status?: string }) =>
    (await axiosInstance.get<ReceivableDocument[]>('/api/commercial-finance/documents', { params })).data,

  getOpenItems: async (asOf?: string) =>
    (await axiosInstance.get<ArOpenItem[]>('/api/commercial-finance/ar/open-items', { params: { asOf } })).data,

  getPayments: async (params?: { customerId?: number; status?: string }) =>
    (await axiosInstance.get<CustomerPayment[]>('/api/commercial-finance/payments', { params })).data,

  createInvoiceFromOrder: async (orderId: number) =>
    (await axiosInstance.post<ReceivableDocument>(
      `/api/commercial-finance/orders/${orderId}/invoices`,
      { documentDate: null, dueDate: null, lines: null },
      withKey(`order-invoice-${orderId}-full`),
    )).data,

  createAdjustment: async (
    invoiceId: number,
    data: CreateReceivableAdjustmentRequest,
    idempotencyKey: string,
  ) => (await axiosInstance.post<ReceivableDocument>(
    `/api/commercial-finance/documents/${invoiceId}/adjustments`,
    data,
    withKey(idempotencyKey),
  )).data,

  issueDocument: async (documentId: number, expectedVersion: number, documentType: string) =>
    (await axiosInstance.post<ReceivableDocument>(
      `/api/commercial-finance/documents/${documentId}/${documentType === 'Invoice' ? 'issue' : 'issue-adjustment'}`, { expectedVersion },
    )).data,

  cancelDocument: async (documentId: number, documentType: string, data: { reason: string; expectedVersion: number }) =>
    (await axiosInstance.post<ReceivableDocument>(
      `/api/commercial-finance/documents/${documentId}/${documentType === 'Invoice' ? 'cancel' : 'cancel-adjustment'}`, data,
    )).data,

  postPayment: async (data: {
    customerId: number;
    commercialCaseId?: number;
    currencyId?: number;
    paymentDate: string;
    amount: number;
    method?: string;
    bankReference?: string;
    allocations: { receivableDocumentId: number; amount: number }[];
  }, idempotencyKey: string) => (await axiosInstance.post<CustomerPayment>(
    '/api/commercial-finance/payments',
    data,
    withKey(idempotencyKey),
  )).data,

  reversePayment: async (paymentId: number, expectedVersion: number, reason: string) =>
    (await axiosInstance.post<CustomerPayment>(
      `/api/commercial-finance/payments/${paymentId}/reverse`,
      { expectedVersion, reason },
    )).data,
};

export default commercialFinanceService;
