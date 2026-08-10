import axiosInstance from '../axiosInstance';

/**
 * Gate 5 Module 5 — FR-MTR-01..05.
 *
 * Mirrors `Backend/ERP_RFQ_Automation/Traceability/TraceabilityContracts.cs`. Every command route
 * requires `Idempotency-Key` and `X-Correlation-ID`; the key is minted once by the calling dialog
 * and reused across retries so a resubmitted quarantine returns the existing hold rather than
 * stacking a second one.
 */

const root = '/api/material-traceability';

const unwrap = <T>(response: { data: T }): T => response.data;

const commandHeaders = (idempotencyKey: string) => ({
  'Idempotency-Key': idempotencyKey,
  'X-Correlation-ID': crypto.randomUUID(),
});

export const LOT_TRACKING_MODES = ['LOT', 'SERIAL', 'UNTRACKED'] as const;
export type LotTrackingMode = (typeof LOT_TRACKING_MODES)[number];

export const LOT_STATUSES = ['AVAILABLE', 'QUARANTINED'] as const;
export type LotStatus = (typeof LOT_STATUSES)[number];

export const QUARANTINE_REASON_CODES = [
  'SUPPLIER_RECALL',
  'QUALITY_HOLD',
  'CERTIFICATE_DEFECT',
  'DAMAGE_SUSPECTED',
  'OTHER',
] as const;
export type QuarantineReasonCode = (typeof QUARANTINE_REASON_CODES)[number];

export const CERTIFICATE_TYPES = [
  'MANUFACTURER',
  'CERTIFICATE_OF_ORIGIN',
  'CERTIFICATE_OF_CONFORMITY',
  'SASO',
  'SABER',
  'MILL_TEST_REPORT',
  'OTHER',
] as const;
export type CertificateType = (typeof CERTIFICATE_TYPES)[number];

/** Derived server-side at every read against today's date. Never stored. */
export type CertificateExpiryState = 'NOT_APPLICABLE' | 'VALID' | 'EXPIRING_SOON' | 'EXPIRED';

export type LotComplianceState = 'COMPLIANT' | 'CERTIFICATE_EXPIRED' | 'NO_CERTIFICATE';

export interface LotSummaryDTO {
  id: number;
  lotNumber: string;
  trackingMode: LotTrackingMode;
  status: LotStatus;
  productId: number;
  partNumber?: string | null;
  productName?: string | null;
  warehouseId: number;
  warehouseName?: string | null;
  quantityReceived: number;
  quantityConsumed: number;
  quantityRemaining: number;
  countryOfOrigin?: string | null;
  manufacturerName?: string | null;
  supplierId: number;
  supplierName?: string | null;
  supplierPurchaseOrderId: number;
  purchaseOrderNumber?: string | null;
  receivedOn: string;
  certificateState: CertificateExpiryState;
  certificateCount: number;
  earliestCertificateExpiry?: string | null;
  commercialCaseId?: number | null;
  nexoraSerial?: string | null;
  version: number;
}

export interface LotCertificateDTO {
  id: number;
  materialLotId: number;
  certificateType: CertificateType;
  certificateNumber?: string | null;
  issuedBy?: string | null;
  issuedOn?: string | null;
  expiresOn?: string | null;
  expiryState: CertificateExpiryState;
  daysUntilExpiry?: number | null;
  attachmentId: number;
  fileName: string;
  contentSha256: string;
  notes?: string | null;
  uploadedOn: string;
  uploadedBy: string;
}

/** A defect the trace reports rather than hides. Never filtered out of the response. */
export interface MaterialTraceGapDTO {
  kind: string;
  subject: string;
  subjectId: number;
  reference: string;
  quantity?: number | null;
  detail: string;
}

export interface LotFulfilmentDTO {
  consumptionId: number;
  orderId: number;
  orderNumber?: string | null;
  orderItemId: number;
  shipmentId?: number | null;
  shipmentNumber?: string | null;
  commercialCaseId?: number | null;
  nexoraSerial?: string | null;
  quantity: number;
  complianceStateAtDeclaration: LotComplianceState;
  complianceOverrideReason?: string | null;
  complianceOverrideBy?: string | null;
  declaredOn: string;
  declaredBy: string;
}

export interface LotWhereFromDTO {
  materialLotId: number;
  lotNumber: string;
  trackingMode: LotTrackingMode;
  status: LotStatus;
  productId: number;
  partNumber?: string | null;
  productName?: string | null;
  warehouseId: number;
  warehouseName?: string | null;
  quantityReceived: number;
  quantityConsumed: number;
  quantityRemaining: number;
  receivedOn: string;
  goodsReceiptId: number;
  receiptNumber?: string | null;
  supplierPurchaseOrderId: number;
  purchaseOrderNumber?: string | null;
  purchaseOrderDemandSource?: string | null;
  supplierPurchaseOrderLineId: number;
  supplierId: number;
  supplierName?: string | null;
  rfqId?: number | null;
  rfqNumber?: string | null;
  commercialCaseId?: number | null;
  nexoraSerial?: string | null;
  countryOfOrigin?: string | null;
  orderedCountryOfOrigin?: string | null;
  manufacturerName?: string | null;
  manufacturerPartNumber?: string | null;
  supplierBatchReference?: string | null;
  manufactureDate?: string | null;
  expiryDate?: string | null;
  hsCode?: string | null;
  quarantineReasonCode?: string | null;
  quarantineReason?: string | null;
  quarantinedOn?: string | null;
  quarantinedBy?: string | null;
  releasedOn?: string | null;
  releasedBy?: string | null;
  releaseReason?: string | null;
  certificateState: CertificateExpiryState;
  certificates: LotCertificateDTO[];
  fulfilledInto: LotFulfilmentDTO[];
  gaps: MaterialTraceGapDTO[];
  version: number;
}

export interface OrderLineLotDTO {
  consumptionId: number;
  materialLotId: number;
  lotNumber: string;
  trackingMode: LotTrackingMode;
  lotStatus: LotStatus;
  quantity: number;
  supplierPurchaseOrderId: number;
  purchaseOrderNumber?: string | null;
  supplierId: number;
  supplierName?: string | null;
  countryOfOrigin?: string | null;
  manufacturerName?: string | null;
  shipmentId?: number | null;
  shipmentNumber?: string | null;
  complianceStateAtDeclaration: LotComplianceState;
  certificateStateNow: CertificateExpiryState;
}

export interface OrderLineTraceDTO {
  orderItemId: number;
  productId?: number | null;
  partNumber?: string | null;
  description?: string | null;
  orderedQuantity: number;
  shippedQuantity: number;
  declaredQuantity: number;
  /** Units that left the building with no lot stated. The number the screen exists for. */
  untracedQuantity: number;
  lots: OrderLineLotDTO[];
}

export interface OrderWhereUsedDTO {
  orderId: number;
  orderNumber: string;
  commercialCaseId?: number | null;
  nexoraSerial?: string | null;
  lines: OrderLineTraceDTO[];
  gaps: MaterialTraceGapDTO[];
}

export interface DisplacedReservationDTO {
  reservationId: number;
  orderId?: number | null;
  orderItemId?: number | null;
  quantity: number;
}

export interface LotQuarantineResultDTO {
  materialLotId: number;
  lotNumber: string;
  status: LotStatus;
  quarantinedQuantity: number;
  inventoryQuarantineQuantity: number;
  availableToPromiseAfter: number;
  displacedReservations: DisplacedReservationDTO[];
  version: number;
  replayed: boolean;
}

export interface LotSearchParams {
  search?: string;
  status?: LotStatus | '';
  supplierId?: number;
  productId?: number;
  warehouseId?: number;
  expiredCertificatesOnly?: boolean;
  limit?: number;
}

const materialTraceabilityService = {
  searchLots: async (params: LotSearchParams = {}): Promise<LotSummaryDTO[]> =>
    unwrap(await axiosInstance.get(`${root}/lots`, {
      params: {
        search: params.search || undefined,
        status: params.status || undefined,
        supplierId: params.supplierId || undefined,
        productId: params.productId || undefined,
        warehouseId: params.warehouseId || undefined,
        expiredCertificatesOnly: params.expiredCertificatesOnly || undefined,
        limit: params.limit ?? 50,
      },
    })),

  getLot: async (materialLotId: number): Promise<LotWhereFromDTO> =>
    unwrap(await axiosInstance.get(`${root}/lots/${materialLotId}`)),

  getOrderTrace: async (orderId: number): Promise<OrderWhereUsedDTO> =>
    unwrap(await axiosInstance.get(`${root}/orders/${orderId}/trace`)),

  listCertificates: async (materialLotId: number): Promise<LotCertificateDTO[]> =>
    unwrap(await axiosInstance.get(`${root}/lots/${materialLotId}/certificates`)),

  attachCertificate: async (
    materialLotId: number,
    request: {
      file: File;
      certificateType: CertificateType;
      certificateNumber?: string;
      issuedBy?: string;
      issuedOn?: string;
      expiresOn?: string;
      notes?: string;
    },
  ): Promise<LotCertificateDTO> => {
    const form = new FormData();
    form.append('file', request.file);
    form.append('certificateType', request.certificateType);
    if (request.certificateNumber) form.append('certificateNumber', request.certificateNumber);
    if (request.issuedBy) form.append('issuedBy', request.issuedBy);
    if (request.issuedOn) form.append('issuedOn', request.issuedOn);
    if (request.expiresOn) form.append('expiresOn', request.expiresOn);
    if (request.notes) form.append('notes', request.notes);
    return unwrap(await axiosInstance.post(`${root}/lots/${materialLotId}/certificates`, form));
  },

  quarantineLot: async (
    materialLotId: number,
    request: {
      expectedVersion: number;
      reasonCode: QuarantineReasonCode;
      reason: string;
      idempotencyKey: string;
    },
  ): Promise<LotQuarantineResultDTO> => {
    const { idempotencyKey, ...body } = request;
    return unwrap(await axiosInstance.post(`${root}/lots/${materialLotId}/quarantine`, body, {
      headers: commandHeaders(idempotencyKey),
    }));
  },

  releaseLot: async (
    materialLotId: number,
    request: { expectedVersion: number; reason: string; idempotencyKey: string },
  ): Promise<LotQuarantineResultDTO> => {
    const { idempotencyKey, ...body } = request;
    return unwrap(await axiosInstance.post(`${root}/lots/${materialLotId}/release`, body, {
      headers: commandHeaders(idempotencyKey),
    }));
  },

  /** The URL the certificate opens from. Digest-verified server-side before a byte is sent. */
  certificateDownloadUrl: (attachmentId: number) => `/api/File/lot-certificate/${attachmentId}`,
};

export default materialTraceabilityService;
