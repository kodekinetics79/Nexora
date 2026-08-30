import type { CreateShipmentDTO } from '../../../api/services/shipmentService';

export interface ShipmentCreationOperation {
  command: CreateShipmentDTO;
  idempotencyKey: string;
}

/** Unknown shipment history is never equivalent to an empty shipment history. */
export const shipmentHistoryIsVerified = (
  isEdit: boolean,
  isSuccess: boolean,
  isFetching: boolean,
) => isEdit || (isSuccess && !isFetching);

/**
 * Once an operation may have reached the server, the exact command and key win over live form
 * state. This is the UI half of the server's request-hash replay contract.
 */
export const operationForShipmentRetry = (
  pending: ShipmentCreationOperation | null,
  next: ShipmentCreationOperation,
) => pending ?? next;
