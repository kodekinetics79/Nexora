import { describe, expect, it } from 'vitest';
import type { QuoteDTO } from '../../../api/services/quoteService';
import { quoteCode, quoteNextMove, quoteStatusWords } from './quoteState';

const quote = (patch: Partial<QuoteDTO>): QuoteDTO => ({
  id: 1, quoteNo: 'QT-0926-0001', sourceLeadRevision: 1, sourceRfqRevision: 1, lifecycleVersion: 1, version: 1,
  businessUnitId: 1, createdBy: 'rep@nexora.sa', itemCount: 2, quoteItems: [], statusCode: 'SENT', statusValue: 'Sent',
  ...patch,
} as QuoteDTO);

const can = { edit: true, enterPo: true };

describe('quoteCode', () => {
  it('reads the code, and a renamed label cannot change it', () => {
    expect(quoteCode({ statusCode: 'SENT', statusValue: 'With customer' })).toBe('SENT');
    expect(quoteCode({ statusCode: null, statusValue: 'sent' } as never)).toBe('SENT');
  });
});

describe('quoteStatusWords', () => {
  it('says Replaced before anything else', () => {
    expect(quoteStatusWords(quote({ supersededByQuoteNo: 'QT-0926-0001-R2', isStale: true })))
      .toMatchObject({ label: 'Replaced', detail: 'By QT-0926-0001-R2' });
  });

  it("shows the client's own step on a sent quote", () => {
    expect(quoteStatusWords(quote({ subStatusKind: 'STEP', subStatusName: 'Technical evaluation', isStale: true })))
      .toMatchObject({ label: 'Technical evaluation', tone: 'info' });
  });

  it("shows the client's own ending in the colour of what it counts as", () => {
    expect(quoteStatusWords(quote({ statusCode: 'ACCEPTED', subStatusKind: 'ENDING', subStatusName: 'Partly won' })))
      .toMatchObject({ label: 'Partly won', tone: 'success' });
    expect(quoteStatusWords(quote({ statusCode: 'REJECTED', outcomeReasonName: 'Price too high' })))
      .toMatchObject({ label: 'Lost', tone: 'error', detail: 'Price too high' });
  });

  it('keeps an ordered quote as Won', () => {
    expect(quoteStatusWords(quote({ statusCode: 'ORDERED' }))).toMatchObject({ label: 'Won', detail: 'Order placed' });
  });

  it('separates replied, no reply and plain sent', () => {
    expect(quoteStatusWords(quote({ respondedOn: '2026-09-20T10:00:00' })).label).toBe('Customer replied');
    expect(quoteStatusWords(quote({ isStale: true, daysSinceSent: 11 }))).toMatchObject({ label: 'No reply', detail: 'Sent 11 days ago' });
    expect(quoteStatusWords(quote({ daysSinceSent: 0 }))).toMatchObject({ label: 'Sent', detail: 'today' });
  });

  it('says a draft with no price is not priced', () => {
    expect(quoteStatusWords(quote({ statusCode: 'DRAFT', currencyId: undefined, totalAmount: 0 })).detail).toBe('Not priced');
  });
});

describe('quoteNextMove', () => {
  it('gives every state exactly one move', () => {
    expect(quoteNextMove(quote({ statusCode: 'DRAFT' }), can).label).toBe('Finish');
    expect(quoteNextMove(quote({ statusCode: 'SENT' }), can).label).toBe('Update status');
    expect(quoteNextMove(quote({ statusCode: 'ACCEPTED' }), can).label).toBe('Enter PO');
    expect(quoteNextMove(quote({ statusCode: 'REJECTED' }), can).label).toBe('View');
    expect(quoteNextMove(quote({ statusCode: 'ORDERED' }), can).label).toBe('View');
  });

  it('never offers work on a replaced quote or to someone who may not do it', () => {
    expect(quoteNextMove(quote({ supersededByQuoteNo: 'QT-0926-0001-R2' }), can).label).toBe('View');
    expect(quoteNextMove(quote({ statusCode: 'SENT' }), { edit: false, enterPo: false }).label).toBe('View');
    expect(quoteNextMove(quote({ statusCode: 'ACCEPTED' }), { edit: true, enterPo: false }).label).toBe('View');
  });
});
