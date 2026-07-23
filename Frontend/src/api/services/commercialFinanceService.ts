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

export interface WriteOffAllocation {
  id: number;
  receivableDocumentId: number;
  documentNumber: string;
  amount: number;
  balanceBefore: number;
  balanceAfter: number;
}

export interface ReceivableWriteOff {
  id: number;
  customerId: number;
  commercialCaseId?: number;
  currencyId?: number;
  currencyCode?: string;
  writeOffNumber?: string;
  status: string;
  accountingDate: string;
  totalAmount: number;
  reasonCode: string;
  reason: string;
  evidenceReference?: string;
  postingStatus: string;
  journalReference?: string;
  version: number;
  createdBy: string;
  createdOn: string;
  approvedBy?: string;
  approvedOn?: string;
  cancelledBy?: string;
  cancelledOn?: string;
  cancellationReason?: string;
  reversedBy?: string;
  reversedOn?: string;
  reversalReason?: string;
  reversalEvidenceReference?: string;
  allocations: WriteOffAllocation[];
}

export interface CustomerRefund {
  id: number;
  sourcePaymentId: number;
  receiptNumber: string;
  customerId: number;
  commercialCaseId?: number;
  currencyId?: number;
  currencyCode?: string;
  refundNumber?: string;
  status: string;
  requestedExecutionDate: string;
  amount: number;
  method: string;
  destinationReference: string;
  destinationVerified: boolean;
  reasonCode: string;
  reason: string;
  evidenceReference?: string;
  postingStatus: string;
  journalReference?: string;
  version: number;
  createdBy: string;
  createdOn: string;
  approvedBy?: string;
  approvedOn?: string;
  releasedBy?: string;
  releasedOn?: string;
  disbursementUpdatedBy?: string;
  disbursementUpdatedOn?: string;
  disbursementFailureReason?: string;
  cancelledBy?: string;
  cancelledOn?: string;
  cancellationReason?: string;
  reversedBy?: string;
  reversedOn?: string;
  reversalReason?: string;
  reversalEvidenceReference?: string;
}

export interface WriteOffEligibility {
  receivableDocumentId: number;
  currentBalance: number;
  pendingAmount: number;
  availableAmount: number;
}

export interface RefundEligibility {
  sourcePaymentId: number;
  paymentAmount: number;
  allocatedAmount: number;
  reservedAmount: number;
  releasedAmount: number;
  availableAmount: number;
}

export interface FinanceExceptionActionRequest {
  expectedVersion: number;
  reason?: string;
  evidenceReference?: string;
}

const withKey = (key: string) => ({ headers: { 'Idempotency-Key': key } });

const commercialFinanceService = {
  getDocuments: async (params?: { customerId?: number; status?: string }) =>
    (await axiosInstance.get<ReceivableDocument[]>('/api/commercial-finance/documents', { params })).data,

  getOpenItems: async (asOf?: string) =>
    (await axiosInstance.get<ArOpenItem[]>('/api/commercial-finance/ar/open-items', { params: { asOf } })).data,

  getPayments: async (params?: { customerId?: number; status?: string }) =>
    (await axiosInstance.get<CustomerPayment[]>('/api/commercial-finance/payments', { params })).data,

  getWriteOffs: async (params?: { customerId?: number; status?: string }) =>
    (await axiosInstance.get<ReceivableWriteOff[]>('/api/commercial-finance/write-offs', { params })).data,

  getWriteOffEligibility: async (documentId: number) =>
    (await axiosInstance.get<WriteOffEligibility>(`/api/commercial-finance/documents/${documentId}/write-off-eligibility`)).data,

  createWriteOff: async (data: {
    accountingDate: string;
    reasonCode: string;
    reason: string;
    evidenceReference?: string;
    allocations: { receivableDocumentId: number; amount: number }[];
  }, idempotencyKey: string) => (await axiosInstance.post<ReceivableWriteOff>(
    '/api/commercial-finance/write-offs', data, withKey(idempotencyKey),
  )).data,

  transitionWriteOff: async (
    writeOffId: number,
    action: 'post' | 'cancel' | 'reverse',
    data: FinanceExceptionActionRequest,
  ) => (await axiosInstance.post<ReceivableWriteOff>(
    `/api/commercial-finance/write-offs/${writeOffId}/${action}`, data,
  )).data,

  getRefunds: async (params?: { customerId?: number; status?: string }) =>
    (await axiosInstance.get<CustomerRefund[]>('/api/commercial-finance/refunds', { params })).data,

  getRefundEligibility: async (paymentId: number) =>
    (await axiosInstance.get<RefundEligibility>(`/api/commercial-finance/payments/${paymentId}/refund-eligibility`)).data,

  createRefund: async (data: {
    sourcePaymentId: number;
    requestedExecutionDate: string;
    amount: number;
    method: string;
    destinationReference: string;
    destinationVerified: boolean;
    reasonCode: string;
    reason: string;
    evidenceReference?: string;
  }, idempotencyKey: string) => (await axiosInstance.post<CustomerRefund>(
    '/api/commercial-finance/refunds', data, withKey(idempotencyKey),
  )).data,

  transitionRefund: async (
    refundId: number,
    action: 'approve' | 'release' | 'cancel' | 'reverse',
    data: FinanceExceptionActionRequest,
  ) => (await axiosInstance.post<CustomerRefund>(
    `/api/commercial-finance/refunds/${refundId}/${action}`, data,
  )).data,

  recordRefundDisbursement: async (
    refundId: number,
    succeeded: boolean,
    data: { expectedVersion: number; providerReference: string; reason?: string },
  ) => (await axiosInstance.post<CustomerRefund>(
    `/api/commercial-finance/refunds/${refundId}/${succeeded ? 'confirm-disbursement' : 'fail-disbursement'}`, data,
  )).data,

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
