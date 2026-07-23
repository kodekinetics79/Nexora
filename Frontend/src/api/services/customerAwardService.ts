import axiosInstance from '../axiosInstance';

export type CustomerPurchaseOrderStatus =
  | 'DRAFT'
  | 'CONFIRMED'
  | 'PARTIALLY_AWARDED'
  | 'FULLY_AWARDED'
  | 'CLOSED'
  | 'CANCELLED';

export type CustomerAwardStatus = 'DRAFT' | 'CONFIRMED' | 'ORDERED' | 'CANCELLED';
export type QuoteAwardOutcome = 'UNAWARDED' | 'PARTIALLY_AWARDED' | 'AWARDED';

export interface CustomerPurchaseOrderLine {
  id: number;
  customerPurchaseOrderId: number;
  externalLineReference: string;
  productId?: number | null;
  description: string;
  orderedQuantity: number;
  uomId?: number | null;
  unitPrice?: number | null;
  lineAmount?: number | null;
  version: number;
}

export interface CustomerPurchaseOrder {
  id: number;
  commercialCaseId: number;
  customerId: number;
  currencyId: number;
  internalNumber: string;
  externalPoNumber: string;
  poDate: string;
  receivedOn: string;
  status: CustomerPurchaseOrderStatus;
  version: number;
  lines: CustomerPurchaseOrderLine[];
}

export interface CustomerAwardLineAllocation {
  id: number;
  customerAwardId: number;
  customerPurchaseOrderLineId: number;
  quoteItemId: number;
  awardedQuantity: number;
  unitPriceSnapshot: number;
  discountSnapshot: number;
  taxSnapshot: number;
  totalSnapshot: number;
  version: number;
}

export interface CustomerAward {
  id: number;
  awardNumber: string;
  customerPurchaseOrderId: number;
  quoteId: number;
  commercialCaseId: number;
  customerId: number;
  currencyId: number;
  status: CustomerAwardStatus;
  version: number;
  confirmedOn?: string | null;
  allocations: CustomerAwardLineAllocation[];
}

export interface CustomerAwardOrder {
  id: number;
  orderNo: string;
  customerAwardId: number;
  status: string;
  version?: number;
}

export interface QuoteAwardBalanceLine {
  quoteItemId: number;
  productId?: number | null;
  productName?: string | null;
  description: string;
  quotedQuantity: number;
  confirmedAwardQuantity: number;
  remainingQuantity: number;
  uomId?: number | null;
  uomCode?: string | null;
  unitPrice: number;
}

export interface QuoteAwardProjection {
  quoteId: number;
  quoteNo: string;
  quoteVersion: number;
  outcome: QuoteAwardOutcome;
  quotedQuantity: number;
  confirmedAwardQuantity: number;
  remainingQuantity: number;
  lines: QuoteAwardBalanceLine[];
  awards: CustomerAward[];
}

export interface CreateCustomerPurchaseOrderCommand {
  quoteId: number;
  commercialCaseId: number;
  customerId: number;
  currencyId: number;
  externalPoNumber: string;
  poDate: string;
  receivedOn: string;
  expectedVersion: 0;
  lines: Array<{
    externalLineReference: string;
    productId?: number | null;
    description: string;
    orderedQuantity: number;
    uomId?: number | null;
    unitPrice?: number | null;
    lineAmount?: number | null;
  }>;
}

export interface CreateCustomerAwardCommand {
  customerPurchaseOrderId: number;
  quoteId: number;
  expectedVersion: 0;
  customerPurchaseOrderExpectedVersion: number;
  quoteExpectedVersion: number;
  allocations: Array<{
    customerPurchaseOrderLineId: number;
    quoteItemId: number;
    awardedQuantity: number;
  }>;
}

export interface VersionedCustomerAwardCommand {
  expectedVersion: number;
}

export interface CancelCustomerAwardCommand extends VersionedCustomerAwardCommand {
  reason: string;
}

export interface CommandIdentity {
  idempotencyKey: string;
  correlationId: string;
}

const commandConfig = ({ idempotencyKey, correlationId }: CommandIdentity) => ({
  headers: {
    'Idempotency-Key': idempotencyKey,
    'X-Correlation-ID': correlationId,
  },
});

const randomId = (): string => {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
};

/** Create once per logical command and retain it when retrying that command. */
export const createCustomerAwardCommandIdentity = (command: string): CommandIdentity => {
  const operationId = randomId();
  return {
    idempotencyKey: `customer-award-${command}-${operationId}`,
    correlationId: operationId,
  };
};

const customerAwardService = {
  getByQuote: async (quoteId: number): Promise<QuoteAwardProjection> =>
    (await axiosInstance.get<QuoteAwardProjection>(`/api/customer-awards/quote/${quoteId}`)).data,

  createPurchaseOrder: async (
    command: CreateCustomerPurchaseOrderCommand,
    identity: CommandIdentity,
  ): Promise<CustomerPurchaseOrder> =>
    (await axiosInstance.post<CustomerPurchaseOrder>(
      '/api/customer-awards/purchase-orders',
      command,
      commandConfig(identity),
    )).data,

  createAward: async (
    command: CreateCustomerAwardCommand,
    identity: CommandIdentity,
  ): Promise<CustomerAward> =>
    (await axiosInstance.post<CustomerAward>(
      '/api/customer-awards',
      command,
      commandConfig(identity),
    )).data,

  confirmAward: async (
    awardId: number,
    command: VersionedCustomerAwardCommand,
    identity: CommandIdentity,
  ): Promise<CustomerAward> =>
    (await axiosInstance.post<CustomerAward>(
      `/api/customer-awards/${awardId}/confirm`,
      command,
      commandConfig(identity),
    )).data,

  cancelAward: async (
    awardId: number,
    command: CancelCustomerAwardCommand,
    identity: CommandIdentity,
  ): Promise<CustomerAward> =>
    (await axiosInstance.post<CustomerAward>(
      `/api/customer-awards/${awardId}/cancel`,
      command,
      commandConfig(identity),
    )).data,

  convertToOrder: async (
    awardId: number,
    command: VersionedCustomerAwardCommand,
    identity: CommandIdentity,
  ): Promise<CustomerAwardOrder> =>
    (await axiosInstance.post<CustomerAwardOrder>(
      `/api/customer-awards/${awardId}/convert-to-order`,
      command,
      commandConfig(identity),
    )).data,
};

export default customerAwardService;
