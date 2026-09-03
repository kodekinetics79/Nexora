import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * A quote that cannot be sent must say so BEFORE the dialog, not after.
 *
 * Half the send chain runs in a background worker whose refusals reach nobody. The screen's own
 * checks could only ever see what the quote screen already had — a stale revision, an all-zero
 * unpriced draft, an underived tax — so three whole classes of refusal were invisible:
 *
 *   - a DRAFT with prices but NO CURRENCY. `isUnpricedDraft` requires currency null AND every
 *     price zero, so this shape sails past the button, produces a green "queued" toast, and dies
 *     in the PDF renderer inside the delivery worker. Both customer quotes on production are
 *     DRAFT with a NULL currency today, and quote 66 has three priced lines with 15% tax
 *     derived — it fails on the currency and nothing else.
 *   - a tenant with no transmitting mailbox, which dead-letters the delivery on attempt ONE.
 *   - a delivery that already ended. The delivery idempotency key is fixed per quote
 *     (`quote:{id}:delivery:v1`), so a dead-lettered row makes that quote number permanently
 *     unsendable and the rep's only move is a new revision.
 *
 * `GET /api/Quote/{id}/send-readiness` answers all of them from the same rules the sender and
 * the renderer apply. These tests pin that the screen shows what it says, in words, with the
 * setup link attached.
 */

const { getById, getPriceAttestation, getSendReadiness } = vi.hoisted(() => ({
  getById: vi.fn(),
  getPriceAttestation: vi.fn(),
  getSendReadiness: vi.fn(),
}));

vi.mock('../../../api/services/quoteService', () => ({
  default: {
    getById,
    getPriceAttestation,
    getSendReadiness,
    sendEmail: vi.fn(),
    transitionStatus: vi.fn(),
    exportPdf: vi.fn(),
    getRevisions: vi.fn().mockResolvedValue([]),
    getRevisionInfo: vi.fn().mockResolvedValue(null),
  },
}));

vi.mock('../../../api/services/procurementService', () => ({
  default: { getWorkbench: vi.fn().mockResolvedValue(null), getRfqIntelligence: vi.fn().mockResolvedValue(null) },
}));

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7 }, hasPermission: () => true }),
}));

vi.mock('react-hot-toast', () => ({
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
  default: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
}));

vi.mock('../../../components/common/CommercialLineIntelligence', () => ({ default: () => null }));
vi.mock('./QuoteOutcomeDialog', () => ({ default: () => null }));
vi.mock('./ExtendValidityDialog', () => ({ default: () => null }));
vi.mock('./PriceConfirmationDialog', () => ({ default: () => null }));
vi.mock('./customer-awards', () => ({ CustomerAwardDialog: () => null }));
vi.mock('../../../components/common/EmailPromptDialog', () => ({ default: () => null }));

import QuoteViewPage from './QuoteViewPage';

/** Production quote 66 (BU 7): DRAFT, three priced lines, 15% tax derived, currency NULL. */
const productionQuote66 = {
  id: 66,
  quoteNo: 'QT-0826-0002',
  statusValue: 'Draft',
  statusCode: 'DRAFT',
  lifecycleVersion: 1,
  currencyId: null,
  totalAmount: 460460,
  quoteDate: '2026-08-26',
  validUntil: '2026-09-30',
  customerName: 'Nexora Pilot Customer',
  quoteItems: [
    { id: 2021, productName: 'Valve assembly', quantity: 1, unitPrice: 18500, discount: 0, totalAmount: 18500, taxAmount: 11100, taxableBase: 18500, taxRatePercentApplied: 15 },
    { id: 2022, productName: 'Flange kit', quantity: 1, unitPrice: 3250, discount: 0, totalAmount: 3250, taxAmount: 5850, taxableBase: 3250, taxRatePercentApplied: 15 },
  ],
};

function renderQuote() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/view/66']}>
        <Routes>
          <Route path="/sales/quotes/view/:id" element={<QuoteViewPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getPriceAttestation.mockResolvedValue({ satisfied: false });
  getById.mockResolvedValue(productionQuote66);
});

describe('QuoteViewPage — the send blockers arrive before the dialog', () => {
  it('names the missing currency, which the screen could never see on its own', async () => {
    getSendReadiness.mockResolvedValue({
      quoteId: 66,
      canSend: false,
      blockers: [{
        code: 'QUOTE_INCOMPLETE',
        message: 'Commercial Review Required: this quote has no currency. Set the currency on the quote before sending it.',
        setupLabel: null,
        setupPath: null,
      }],
    });
    renderQuote();

    const send = await screen.findByRole('button', { name: /send to customer/i });
    expect(send).toBeDisabled();
    expect(await screen.findByText(/this quote has no currency/i)).toBeVisible();
  });

  it('lists every blocker with its setup link, not just the first', async () => {
    // A rep who fixes one, comes back and is stopped by the next has made a round trip for
    // nothing — and an incomplete draft usually has more than one thing missing.
    getSendReadiness.mockResolvedValue({
      quoteId: 66,
      canSend: false,
      blockers: [
        { code: 'QUOTE_INCOMPLETE', message: 'Commercial Review Required: this quote has no currency. Set the currency on the quote before sending it.' },
        {
          code: 'OUTBOUND_MAIL_NOT_CONFIGURED',
          message: 'Nothing can be emailed to customers yet: this tenant has no active SMTP mailbox and the platform sender does not transmit.',
          setupLabel: 'Setup → Mailboxes',
          setupPath: '/setup/mailboxes',
        },
      ],
    });
    renderQuote();

    await screen.findByRole('button', { name: /send to customer/i });
    expect(screen.getByText(/this quote has no currency/i)).toBeVisible();
    expect(screen.getByText(/no active SMTP mailbox/i)).toBeVisible();
    expect(screen.getByRole('link', { name: /open setup → mailboxes/i }))
      .toHaveAttribute('href', '/setup/mailboxes');
  });

  it('says the customer may already hold a quote whose delivery was interrupted', async () => {
    getSendReadiness.mockResolvedValue({
      quoteId: 66,
      canSend: false,
      deliveryOutcome: 'UNCERTAIN',
      blockers: [{
        code: 'DELIVERY_OUTCOME_UNCERTAIN',
        message: 'Delivery of this quote was interrupted and never confirmed either way, so the customer may or may not have received it. Nothing was resent automatically, on purpose. Check with the customer; if it did not arrive, issue this quote as a new revision and send that.',
      }],
    });
    renderQuote();

    const send = await screen.findByRole('button', { name: /send to customer/i });
    expect(send).toBeDisabled();
    expect(await screen.findByText(/may or may not have received it/i)).toBeVisible();
    expect(screen.getByText(/new revision/i)).toBeVisible();
  });

  it('keeps Send enabled and silent when the server reports the quote is ready', async () => {
    // THE CONTROL. A readiness check that always blocks would pass all three tests above and
    // stop the product working.
    getSendReadiness.mockResolvedValue({ quoteId: 66, canSend: true, blockers: [] });
    renderQuote();

    const send = await screen.findByRole('button', { name: /send to customer/i });
    expect(send).toBeEnabled();
    expect(screen.queryByText(/no currency|SMTP mailbox|may or may not/i)).not.toBeInTheDocument();
  });
});
