import axiosInstance from '../axiosInstance';

export interface ProcurementHandoff {
  id: number;
  customerOrderId: number;
  customerOrderNumber: string;
  customerOrderLineId: number;
  commercialDemandLineId: number;
  sourcingAwardId: number;
  supplierQuotedItemId: number;
  supplierId: number;
  supplierName: string;
  nexoraSerial: string;
  requiredQuantity: number;
  selectedUnitCost: number;
  currencyId: number;
  currencyCode: string;
  requiredOn?: string | null;
  destinationType: 'WAREHOUSE' | 'DROP_SHIP';
  warehouseId?: number | null;
  deliveryLocation?: string | null;
  externalSystemTarget: string;
  status: string;
  externalSupplierPoNumber?: string | null;
  externalSupplierPoLineNumber?: string | null;
  externalOrderedQuantity?: number | null;
  externalApprovedUnitCost?: number | null;
  externalExpectedOn?: string | null;
  externalStatus?: string | null;
  lastSynchronizedOn?: string | null;
  sourceOfTruth?: string | null;
  isAuthoritative: boolean;
  version: number;
}

export interface ProcurementHandoffCandidate {
  customerOrderId: number;
  customerOrderNumber: string;
  customerOrderLineId: number;
  supplierName: string;
  nexoraSerial: string;
  requiredQuantity: number;
  selectedUnitCost: number;
  currencyCode: string;
}

const commandHeaders = (key: string) => ({
  headers: { 'Idempotency-Key': key, 'X-Correlation-ID': crypto.randomUUID() },
});

const procurementHandoffService = {
  candidates: async (): Promise<ProcurementHandoffCandidate[]> =>
    (await axiosInstance.get<ProcurementHandoffCandidate[]>('/api/procurement-handoffs/candidates')).data,
  search: async (search = '', customerOrderId?: number): Promise<ProcurementHandoff[]> =>
    (await axiosInstance.get<ProcurementHandoff[]>('/api/procurement-handoffs', {
      params: { search: search.trim() || undefined, customerOrderId, limit: 100 },
    })).data,
  create: async (command: {
    customerOrderLineId: number;
    destinationType: 'WAREHOUSE' | 'DROP_SHIP';
    warehouseId?: number | null;
    deliveryLocation?: string | null;
    requiredOn?: string | null;
  }, key: string): Promise<ProcurementHandoff> =>
    (await axiosInstance.post<ProcurementHandoff>('/api/procurement-handoffs', command, commandHeaders(key))).data,
  synchronize: async (id: number, command: {
    expectedVersion: number;
    externalSupplierPoNumber: string;
    externalSupplierPoLineNumber: string;
    orderedQuantity: number;
    approvedUnitCost: number;
    expectedOn: string;
    status: string;
    synchronizedOn: string;
  }, key: string): Promise<ProcurementHandoff> =>
    (await axiosInstance.post<ProcurementHandoff>(
      `/api/procurement-handoffs/${id}/synchronize`, command, commandHeaders(key),
    )).data,
};

export default procurementHandoffService;
