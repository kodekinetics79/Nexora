import axiosInstance from "../axiosInstance";

/**
 * FR-MAS-01. The six inbound milestones plus the terminal cancellation, as the server enforces
 * them.
 *
 * The list is mirrored here for typing and labelling only — it is NEVER used to decide what a
 * shipment may do next. Every shipment carries `allowedNextMilestones`, computed server-side, and
 * the UI offers exactly those. A client that re-implemented the ladder would eventually disagree
 * with the server about what is legal, and the user would find out via a 409.
 */
export const INBOUND_MILESTONES = [
  "READY_AT_FACTORY",
  "DEPARTED_ORIGIN",
  "IN_TRANSIT",
  "ARRIVED_SAUDI_PORT",
  "CUSTOMS_CLEARANCE",
  "RECEIVED_AT_WAREHOUSE",
  "CANCELLED",
] as const;

export type InboundMilestone = (typeof INBOUND_MILESTONES)[number];

export const MILESTONE_LABELS: Record<InboundMilestone, string> = {
  READY_AT_FACTORY: "Ready at factory",
  DEPARTED_ORIGIN: "Departed origin",
  IN_TRANSIT: "In transit",
  ARRIVED_SAUDI_PORT: "Arrived Saudi port",
  CUSTOMS_CLEARANCE: "Customs clearance",
  RECEIVED_AT_WAREHOUSE: "Received at warehouse",
  CANCELLED: "Cancelled",
};

export type PortOfEntryKind =
  | "SEAPORT"
  | "AIRPORT"
  | "DRY_PORT"
  | "LAND_BORDER";

export const PORT_KIND_LABELS: Record<PortOfEntryKind, string> = {
  SEAPORT: "Seaport",
  AIRPORT: "Airport",
  DRY_PORT: "Dry port",
  LAND_BORDER: "Land border",
};

export interface PortOfEntry {
  id: number;
  code: string;
  name: string;
  kind: PortOfEntryKind;
  countryCode: string;
  city?: string | null;
  isActive: boolean;
  version: number;
}

/**
 * FR-MAS-03. The derived Material Available Date together with everything that produced it.
 *
 * `date` is null whenever it could not be derived, and `unavailableReason` then carries the
 * sentence to show instead. There is deliberately no fallback value: a date the system guessed is
 * indistinguishable on screen from one it calculated, and a customer promise gets made against it.
 */
export interface MaterialAvailability {
  date?: string | null;
  basisKind?: string | null;
  basisDate?: string | null;
  customsClearanceDays?: number | null;
  putawayDays?: number | null;
  computedOn?: string | null;
  unavailableReason?: string | null;
}

export interface InboundShipmentLine {
  id: number;
  purchaseOrderLineId: number;
  productId: number;
  description: string;
  shippedQuantity: number;
  orderedQuantity: number;
  /** FR-MAS-05. The running total across every non-cancelled shipment for this PO line. */
  purchaseOrderLineShippedQuantity: number;
  purchaseOrderLineReceivedQuantity: number;
  /**
   * How much of this shipment line a goods receipt has actually booked in. Written only by the
   * goods receipt path — recording the "received at warehouse" milestone does not touch it.
   */
  receivedQuantity: number;
  outstandingReceiptQuantity: number;
}

/**
 * Where a shipment stands against the goods receipts posted for it.
 *
 * `RECEIVED_AT_WAREHOUSE` means a truck reached the gate. It does NOT mean stock was booked in:
 * that is the goods receipt, which counts what actually arrived, names the lot or serials, and
 * moves quantity on hand. A shipment showing "received at warehouse" and `NOT_RECEIPTED` is the
 * honest state, and the panel says so rather than letting the milestone imply a receipt.
 */
export type InboundReceiptState =
  | "NOT_RECEIPTED"
  | "PARTIALLY_RECEIPTED"
  | "RECEIPTED"
  | "NOT_APPLICABLE";

export const RECEIPT_STATE_LABELS: Record<InboundReceiptState, string> = {
  NOT_RECEIPTED: "Not booked in",
  PARTIALLY_RECEIPTED: "Part booked in",
  RECEIPTED: "Booked in",
  NOT_APPLICABLE: "—",
};

export interface InboundShipmentMilestoneEvent {
  eventType: string;
  milestone?: InboundMilestone | null;
  occurredOn?: string | null;
  actor: string;
  recordedOn: string;
  note?: string | null;
}

export interface InboundShipment {
  id: number;
  purchaseOrderId: number;
  purchaseOrderNumber: string;
  shipmentNumber: string;
  milestone: InboundMilestone;
  milestoneOccurredOn: string;
  readyAtFactoryOn?: string | null;
  departedOriginOn?: string | null;
  inTransitOn?: string | null;
  arrivedSaudiPortOn?: string | null;
  customsClearanceOn?: string | null;
  receivedAtWarehouseOn?: string | null;
  cancelledOn?: string | null;
  cancellationReason?: string | null;
  portOfEntryId?: number | null;
  portOfEntryName?: string | null;
  carrierName?: string | null;
  trackingReference?: string | null;
  /** MANUAL until a carrier integration exists. See the gate report: the API half is not built. */
  trackingSource: "MANUAL" | "CARRIER_API";
  etaDate?: string | null;
  etaUpdatedOn?: string | null;
  etaUpdatedBy?: string | null;
  materialAvailability: MaterialAvailability;
  committedShipDate?: string | null;
  customerRequiredOn?: string | null;
  /** The Gate 4/Gate 5 seam, made visible. See {@link InboundReceiptState}. */
  receiptState: InboundReceiptState;
  receiptedQuantity: number;
  outstandingReceiptQuantity: number;
  version: number;
  lines: InboundShipmentLine[];
  history: InboundShipmentMilestoneEvent[];
  /** The server's own answer to "what can this shipment do next". Drive the UI from this. */
  allowedNextMilestones: InboundMilestone[];
}

export interface InboundLogisticsPolicy {
  businessUnitId: number;
  /**
   * WORKING days. Null means NOT CONFIGURED, not zero — and while it is null no Material Available
   * Date is derived anywhere in the tenant. Zero is a different, deliberate assertion.
   */
  customsClearanceLeadDays?: number | null;
  putawayLeadDays?: number | null;
  isConfigured: boolean;
  version: number;
  modifiedOn?: string | null;
  modifiedBy?: string | null;
  isDefault: boolean;
}

const unwrap = <T>(response: { data: T }): T => response.data;
const commandHeaders = (key: string) => ({
  "Idempotency-Key": key,
  "X-Correlation-ID": crypto.randomUUID(),
});

const inboundShipmentService = {
  getForPurchaseOrder: async (purchaseOrderId: number): Promise<InboundShipment[]> =>
    unwrap(
      await axiosInstance.get<InboundShipment[]>(
        `/api/inbound-shipments/purchase-orders/${purchaseOrderId}`,
      ),
    ),

  create: async (request: {
    purchaseOrderId: number;
    lines: Array<{ purchaseOrderLineId: number; quantity: number }>;
    carrierName?: string | null;
    trackingReference?: string | null;
    etaDate?: string | null;
    portOfEntryId?: number | null;
    readyAtFactoryOn?: string | null;
    idempotencyKey: string;
  }): Promise<InboundShipment> => {
    const { idempotencyKey, ...body } = request;
    return unwrap(
      await axiosInstance.post<InboundShipment>("/api/inbound-shipments", body, {
        headers: commandHeaders(idempotencyKey),
      }),
    );
  },

  /**
   * FR-MAS-01. Records a milestone.
   *
   * Nothing is validated here that the server does not validate itself — the ladder only moves
   * forward, an arrival needs a named entry location, a cancellation needs a reason, and a milestone
   * cannot be dated before the previous one or in the future. The caller surfaces the server's own
   * refusal rather than paraphrasing it.
   */
  recordMilestone: async (
    shipmentId: number,
    request: {
      expectedVersion: number;
      milestone: InboundMilestone;
      /** Calendar date, `YYYY-MM-DD`. Held as a date, not an instant. */
      occurredOn: string;
      portOfEntryId?: number | null;
      etaDate?: string | null;
      note?: string | null;
      idempotencyKey: string;
    },
  ): Promise<InboundShipment> => {
    const { idempotencyKey, ...body } = request;
    return unwrap(
      await axiosInstance.post<InboundShipment>(
        `/api/inbound-shipments/${shipmentId}/milestones`,
        body,
        { headers: commandHeaders(idempotencyKey) },
      ),
    );
  },

  /** FR-MAS-02, manual path. `clearEta` exists because null on the wire cannot mean both
   * "unchanged" and "erase". */
  updateTracking: async (
    shipmentId: number,
    request: {
      expectedVersion: number;
      carrierName?: string | null;
      trackingReference?: string | null;
      etaDate?: string | null;
      clearEta?: boolean;
      note?: string | null;
      idempotencyKey: string;
    },
  ): Promise<InboundShipment> => {
    const { idempotencyKey, ...body } = request;
    return unwrap(
      await axiosInstance.post<InboundShipment>(
        `/api/inbound-shipments/${shipmentId}/tracking`,
        body,
        { headers: commandHeaders(idempotencyKey) },
      ),
    );
  },

  getPortsOfEntry: async (includeInactive = false): Promise<PortOfEntry[]> =>
    unwrap(
      await axiosInstance.get<PortOfEntry[]>("/api/inbound-shipments/ports-of-entry", {
        params: { includeInactive },
      }),
    ),

  createPortOfEntry: async (request: {
    code: string;
    name: string;
    kind: PortOfEntryKind;
    countryCode?: string;
    city?: string | null;
    idempotencyKey: string;
  }): Promise<PortOfEntry> => {
    const { idempotencyKey, ...body } = request;
    return unwrap(
      await axiosInstance.post<PortOfEntry>("/api/inbound-shipments/ports-of-entry", body, {
        headers: commandHeaders(idempotencyKey),
      }),
    );
  },

  /**
   * Loads the canonical Saudi entry points into this tenant. Nothing is seeded automatically, and
   * codes the tenant already holds are left untouched, so a re-import cannot undo a local edit.
   */
  importStandardSaudiPorts: async (idempotencyKey: string): Promise<PortOfEntry[]> =>
    unwrap(
      await axiosInstance.post<PortOfEntry[]>(
        "/api/inbound-shipments/ports-of-entry/import-standard",
        {},
        { headers: commandHeaders(idempotencyKey) },
      ),
    ),

  getPolicy: async (): Promise<InboundLogisticsPolicy> =>
    unwrap(await axiosInstance.get<InboundLogisticsPolicy>("/api/inbound-shipments/policy")),

  updatePolicy: async (request: {
    customsClearanceLeadDays?: number | null;
    putawayLeadDays?: number | null;
    clearLeadTimes?: boolean;
    idempotencyKey: string;
  }): Promise<InboundLogisticsPolicy> => {
    const { idempotencyKey, ...body } = request;
    return unwrap(
      await axiosInstance.put<InboundLogisticsPolicy>("/api/inbound-shipments/policy", body, {
        headers: commandHeaders(idempotencyKey),
      }),
    );
  },
};

export default inboundShipmentService;
