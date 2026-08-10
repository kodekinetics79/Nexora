import axiosInstance from '../axiosInstance';

/**
 * Gate 7 / Module 7 — outbound delivery, proof of delivery and delivery exceptions.
 *
 * Every quantity on these types is produced by the server. Nothing in this file, and nothing that
 * consumes it, subtracts one server number from another to make a third: the delivery note is a
 * document someone signs at a receiving bay, and a figure computed in a browser has no record
 * behind it when the signed copy is produced in a dispute.
 */

/** FR-DLM-05. The governed lifecycle. Not the tenant's own `status` picklist label. */
export const DELIVERY_STATUS = {
  Scheduled: 'SCHEDULED',
  Dispatched: 'DISPATCHED',
  InTransit: 'IN_TRANSIT',
  Delivered: 'DELIVERED',
  DeliveryException: 'DELIVERY_EXCEPTION',
  Cancelled: 'CANCELLED',
} as const;

export type DeliveryStatus = (typeof DELIVERY_STATUS)[keyof typeof DELIVERY_STATUS];

export const DELIVERY_STATUS_LABEL: Record<string, string> = {
  SCHEDULED: 'Scheduled',
  DISPATCHED: 'Dispatched',
  IN_TRANSIT: 'In transit',
  DELIVERED: 'Delivered',
  DELIVERY_EXCEPTION: 'Delivery exception',
  CANCELLED: 'Cancelled',
};

/**
 * The only transitions an operator may choose. DELIVERED and DELIVERY_EXCEPTION are absent on
 * purpose — they are outcomes of what the customer accepted, applied by the confirmation, and the
 * server refuses them here too. This list exists so the UI cannot offer a button the server will
 * reject; it is not the authority.
 */
export const OPERATOR_TRANSITIONS: Record<string, DeliveryStatus[]> = {
  SCHEDULED: ['DISPATCHED', 'CANCELLED'],
  DISPATCHED: ['IN_TRANSIT', 'CANCELLED'],
  IN_TRANSIT: ['CANCELLED'],
  DELIVERED: [],
  DELIVERY_EXCEPTION: [],
  CANCELLED: [],
};

/** FR-DLM-07. Why a receiving bay took fewer units than the note declared. */
export const DELIVERY_EXCEPTION_REASONS = [
  { code: 'SHORT_SHIPMENT', label: 'Short shipment — fewer units arrived than declared' },
  { code: 'DAMAGED', label: 'Damaged — units arrived and were visibly damaged' },
  { code: 'REJECTED', label: 'Rejected — units arrived intact and were refused' },
  { code: 'LOST_IN_TRANSIT', label: 'Lost in transit — the carrier cannot account for them' },
] as const;

export const SHORTFALL_DECISIONS = [
  { code: 'RESUPPLY', label: 'Re-supply — send the balance against the same order line' },
  { code: 'CREDIT', label: 'Credit — the shortfall will not be re-supplied' },
] as const;

export interface DeliveredQuantityLine {
  orderItemId: number;
  awardedQuantity: number;
  despatchedQuantity: number;
  acceptedQuantity: number;
  awaitingConfirmationQuantity: number;
  refusedQuantity: number;
  outstandingQuantity: number;
  isFullyDelivered: boolean;
}

export interface DeliveryProofLineView {
  id: number;
  shipmentItemId: number;
  orderItemId: number;
  productName?: string | null;
  despatchedQuantity: number;
  acceptedQuantity: number;
  refusedQuantity: number;
  exceptionReasonCode?: string | null;
  exceptionNote?: string | null;
  notes?: string | null;
  commercialDecision?: string | null;
  commercialDecisionReason?: string | null;
  commercialDecisionBy?: string | null;
  commercialDecisionOn?: string | null;
}

export interface DeliveryProofView {
  id: number;
  shipmentId: number;
  shipmentNo: string;
  outcome: string;
  receivedByName: string;
  receivedByContact?: string | null;
  receivedByPosition?: string | null;
  receivedOn: string;
  signatureEvidenceId?: number | null;
  stampEvidenceId?: number | null;
  photoEvidenceId?: number | null;
  gpsLatitude?: number | null;
  gpsLongitude?: number | null;
  gpsAccuracyMeters?: number | null;
  gpsCapturedOn?: string | null;
  hasGpsFix: boolean;
  notes?: string | null;
  commercialCaseId?: number | null;
  nexoraSerial?: string | null;
  recordedBy: string;
  recordedOn: string;
  lines: DeliveryProofLineView[];
}

export interface DeliveryNoteLot {
  lotNumber: string;
  quantity: number;
  countryOfOrigin?: string | null;
}

export interface DeliveryNoteLine {
  shipmentItemId: number;
  orderItemId: number;
  productName?: string | null;
  description?: string | null;
  unitOfMeasure?: string | null;
  orderedQuantity: number;
  despatchedQuantity: number;
  previouslyAcceptedQuantity: number;
  remainingQuantity: number;
  lots: DeliveryNoteLot[];
}

export interface IssuerIdentity {
  legalName?: string | null;
  taxRegistrationNumber?: string | null;
  addressLine?: string | null;
  phone?: string | null;
  email?: string | null;
}

export interface DeliveryNoteView {
  shipmentId: number;
  shipmentNo: string;
  shipmentDate: string;
  deliveryStatus: string;
  orderId: number;
  orderNo: string;
  commercialCaseId?: number | null;
  nexoraSerial?: string | null;
  customerName?: string | null;
  customerPurchaseOrderNumber?: string | null;
  customerPurchaseOrderDate?: string | null;
  shippingAddress?: string | null;
  deliveryCityId?: number | null;
  deliveryCityName?: string | null;
  deliveryRegionName?: string | null;
  deliveryCountryName?: string | null;
  carrier?: string | null;
  trackingNumber?: string | null;
  issuer: IssuerIdentity;
  lines: DeliveryNoteLine[];
  proof?: DeliveryProofView | null;
  /** Named absences. Rendered on the document, never swallowed. */
  gaps: string[];
}

export interface ConfirmDeliveryLineCommand {
  shipmentItemId: number;
  acceptedQuantity: number;
  exceptionReasonCode?: string | null;
  exceptionNote?: string | null;
  notes?: string | null;
}

export interface ConfirmDeliveryCommand {
  receivedByName: string;
  receivedByContact?: string | null;
  receivedByPosition?: string | null;
  receivedOn: string;
  signatureEvidenceId?: number | null;
  stampEvidenceId?: number | null;
  photoEvidenceId?: number | null;
  gpsLatitude?: number | null;
  gpsLongitude?: number | null;
  gpsAccuracyMeters?: number | null;
  gpsCapturedOn?: string | null;
  notes?: string | null;
  lines: ConfirmDeliveryLineCommand[];
}

export interface DeliveryProofEvidenceView {
  attachmentId: number;
  fileName: string;
  mimeType?: string | null;
  fileSize: number;
  contentSha256: string;
}

const deliveryService = {
  /** FR-DLM-01. The note, assembled server-side. */
  getNote: async (shipmentId: number) => {
    const response = await axiosInstance.get<DeliveryNoteView>(
      `/api/delivery/shipments/${shipmentId}/note`);
    return response.data;
  },

  /** FR-DLM-02. Awarded, despatched, awaiting, accepted and refused, per order line. */
  getDeliveredQuantities: async (orderId: number) => {
    const response = await axiosInstance.get<DeliveredQuantityLine[]>(
      `/api/delivery/orders/${orderId}/delivered-quantities`);
    return response.data;
  },

  /** FR-DLM-05. */
  transition: async (shipmentId: number, status: DeliveryStatus) => {
    const response = await axiosInstance.post<{ shipmentId: number; deliveryStatus: string }>(
      `/api/delivery/shipments/${shipmentId}/status`, { status });
    return response.data;
  },

  /**
   * FR-DLM-03. Sends the bytes through the shared inspection and content-addressed evidence
   * pipeline, and returns the attachment id the confirmation then references. Uploading is
   * separate from confirming so a failed scan on the third photograph cannot roll back accepted
   * quantities the customer has already agreed to.
   */
  captureEvidence: async (shipmentId: number, kind: 'SIGNATURE' | 'STAMP' | 'PHOTO', file: File) => {
    const form = new FormData();
    form.append('file', file);
    form.append('kind', kind);
    const response = await axiosInstance.post<DeliveryProofEvidenceView>(
      `/api/delivery/shipments/${shipmentId}/proof-evidence`, form);
    return response.data;
  },

  /** FR-DLM-03 and FR-DLM-02, in one write. The outcome is derived server-side. */
  confirm: async (shipmentId: number, idempotencyKey: string, command: ConfirmDeliveryCommand) => {
    const response = await axiosInstance.post<DeliveryProofView>(
      `/api/delivery/shipments/${shipmentId}/confirmation`, command,
      { headers: { 'Idempotency-Key': idempotencyKey } });
    return response.data;
  },

  getConfirmation: async (shipmentId: number) => {
    const response = await axiosInstance.get<DeliveryProofView>(
      `/api/delivery/shipments/${shipmentId}/confirmation`);
    return response.data;
  },

  /** FR-DLM-07, commercial half. Append-only: a shortfall is decided once. */
  recordShortfallDecision: async (
    proofLineId: number, decision: 'RESUPPLY' | 'CREDIT', reason: string) => {
    const response = await axiosInstance.post<DeliveryProofLineView>(
      `/api/delivery/shortfalls/${proofLineId}/decision`, { decision, reason });
    return response.data;
  },

  /** The digest-verifying download route. POD evidence is served nowhere else. */
  evidenceUrl: (attachmentId: number) => `/api/File/delivery-proof/${attachmentId}`,
};

export default deliveryService;
