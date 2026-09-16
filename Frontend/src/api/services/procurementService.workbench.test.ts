import { describe, expect, it } from 'vitest';
import { normalizeWorkbench } from './procurementService';

/**
 * The server's workbench JSON (captured live 2026-09-15) carries `rfqItemIds` on each
 * solicitation. The screens read `requestedRfqItemIds`. This pins the translation so the two
 * names can never drift apart again without a red test.
 */
describe('normalizeWorkbench', () => {
  const base = {
    rfqId: 1, rfqNumber: 'NXR-RFQ-1', nexoraSerial: 'E2E-1', customerName: 'Al Jazirah', currencyCode: 'SAR',
    lines: [], offers: [], awards: [], purchaseOrders: [], customerQuoteDraft: null,
  } as any;

  it("maps the server's rfqItemIds onto requestedRfqItemIds", () => {
    const out = normalizeWorkbench({
      ...base,
      solicitations: [{ id: 1, rfqId: 1, supplierId: 1, supplierName: 'Gulf Switchgear', status: 'SENT',
        channel: 'Email', attemptCount: 1, updatedOn: '2026-09-16T03:02:22Z', rfqItemIds: [1] }],
    });
    expect(out.solicitations[0].requestedRfqItemIds).toEqual([1]);
  });

  it('keeps requestedRfqItemIds when the server already sends that name, and never yields undefined', () => {
    const out = normalizeWorkbench({
      ...base,
      solicitations: [
        { id: 1, requestedRfqItemIds: [4, 5] },
        { id: 2 },
      ] as any,
    });
    expect(out.solicitations[0].requestedRfqItemIds).toEqual([4, 5]);
    expect(out.solicitations[1].requestedRfqItemIds).toEqual([]);
  });
});
