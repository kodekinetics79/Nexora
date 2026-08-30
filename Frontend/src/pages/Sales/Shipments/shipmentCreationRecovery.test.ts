import { describe, expect, it } from 'vitest';
import {
  operationForShipmentRetry,
  shipmentHistoryIsVerified,
  type ShipmentCreationOperation,
} from './shipmentCreationRecovery';

const operation = (quantity: number, key: string): ShipmentCreationOperation => ({
  idempotencyKey: key,
  command: {
    orderId: 900,
    businessUnitId: 1,
    statusId: 21,
    shipmentDate: '2026-08-29',
    carrier: 'Synthetic carrier',
    serviceLevel: 'Synthetic service',
    trackingNumber: 'SYNTH-1',
    shippingAddress: '1 Synthetic Way',
    notes: '',
    items: [{ orderItemId: 5001, quantity }],
  },
});

describe('shipment creation recovery contract', () => {
  it('does not calculate availability while prior shipment history is loading or failed', () => {
    expect(shipmentHistoryIsVerified(false, false, true)).toBe(false);
    expect(shipmentHistoryIsVerified(false, false, false)).toBe(false);
    expect(shipmentHistoryIsVerified(false, true, false)).toBe(true);
  });

  it('replays the frozen command and key rather than changed live form state', () => {
    const frozen = operation(4, 'shipment-key-1');
    const editedForm = operation(7, 'shipment-key-2');

    expect(operationForShipmentRetry(frozen, editedForm)).toBe(frozen);
    expect(operationForShipmentRetry(frozen, editedForm)).toEqual(operation(4, 'shipment-key-1'));
  });
});
