import { describe, expect, it } from 'vitest';
import { normalizeWorkbench, type SourcingWorkbench } from './procurementService';

/**
 * The server's SolicitationView calls the lines a supplier was asked about `rfqItemIds`; the
 * workbench's readers call them `requestedRfqItemIds`. Without the mapping every asked line
 * looked untouched and the Coverage step said "no supplier has been asked" about lines that were.
 */
describe('normalizeWorkbench', () => {
  const base: SourcingWorkbench = {
    rfqId: 5, rfqNumber: 'RFQ-5', lines: [], solicitations: [], offers: [], awards: [], purchaseOrders: [], customerQuoteDraft: null,
  };
  const solicitation = {
    id: 1, rfqId: 5, supplierId: 8, supplierName: 'Asked Supplier', status: 'SENT' as const, channel: 'EMAIL',
    attemptCount: 1, updatedOn: '2026-09-15T08:00:00Z', dueOn: '2026-09-22T12:00:00Z',
  };

  it("maps the server's rfqItemIds onto requestedRfqItemIds and keeps everything else", () => {
    const wire = { ...base, solicitations: [{ ...solicitation, rfqItemIds: [11, 12] }] } as unknown as SourcingWorkbench;
    const [normalized] = normalizeWorkbench(wire).solicitations;
    expect(normalized.requestedRfqItemIds).toEqual([11, 12]);
    expect(normalized.dueOn).toBe('2026-09-22T12:00:00Z');
    expect(normalized.status).toBe('SENT');
  });

  it('leaves a requestedRfqItemIds already present alone', () => {
    const wire = { ...base, solicitations: [{ ...solicitation, requestedRfqItemIds: [11], rfqItemIds: [99] }] } as unknown as SourcingWorkbench;
    expect(normalizeWorkbench(wire).solicitations[0].requestedRfqItemIds).toEqual([11]);
  });

  it('never leaves the field undefined', () => {
    const wire = { ...base, solicitations: [solicitation] } as unknown as SourcingWorkbench;
    expect(normalizeWorkbench(wire).solicitations[0].requestedRfqItemIds).toEqual([]);
  });
});
