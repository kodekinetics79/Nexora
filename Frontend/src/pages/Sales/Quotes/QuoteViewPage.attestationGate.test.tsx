import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * The price-source confirmation (R5) is done INSIDE the send flow: Send → recipient → confirm
 * the price source → the quote goes. The readiness endpoint lists that confirmation as a
 * blocker, and the screen disabled Send on any blocker, so a quote whose only remaining item
 * was "confirm the price source" could not be sent from its own screen — the confirmation
 * dialog was unreachable. Found driving a real quote on 2026-09-15. These tests pin: that
 * blocker alone keeps Send enabled and is worded as the step Send takes; with any other
 * blocker present, Send is still disabled.
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
  useAuth: () => ({ userData: { businessUnitId: 1 }, hasPermission: () => true }),
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

/** QT-0926-0001 as it stood on 2026-09-15: priced, dated, formatted, currency set. */
const readyQuote = {
  id: 1, quoteNo: 'QT-0926-0001', statusValue: 'Draft', statusCode: 'DRAFT', lifecycleVersion: 1,
  currencyId: 1, currencyCode: 'SAR', totalAmount: 134276.88, quoteDate: '2026-09-16', validUntil: '2026-10-15',
  customerName: 'Al Jazirah Petrochemical Company',
  quoteItems: [
    { id: 1, productName: 'CIRCUIT BREAKER', quantity: 12, unitPrice: 2338.54, discount: 0, totalAmount: 32271.88, taxAmount: 4209.38, taxableBase: 28062.5, taxRatePercentApplied: 15 },
  ],
};
const attestation = {
  code: 'PRICE_ATTESTATION_REQUIRED',
  message: 'The price source has not been confirmed for this quote. Confirm where the prices came from — your sales manager, or a supplier quote — before sending it.',
};

function renderQuote() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/view/1']}>
        <Routes><Route path="/sales/quotes/view/:id" element={<QuoteViewPage />} /></Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getPriceAttestation.mockResolvedValue({ satisfied: false });
  getById.mockResolvedValue(readyQuote);
});

describe('QuoteViewPage — the price-source confirmation never locks the door it lives behind', () => {
  it('keeps Send enabled when confirming the price source is the only thing left, and says so', async () => {
    getSendReadiness.mockResolvedValue({ quoteId: 1, canSend: false, blockers: [attestation] });
    renderQuote();

    expect(await screen.findByText(/press send to customer; you confirm it there/i)).toBeVisible();
    expect(screen.getByRole('button', { name: /^send to customer$/i })).toBeEnabled();
  });

  it('still disables Send while any other blocker remains', async () => {
    getSendReadiness.mockResolvedValue({
      quoteId: 1, canSend: false,
      blockers: [
        attestation,
        { code: 'QUOTE_FORMAT_INCOMPLETE', message: 'This quotation cannot be produced because no company address, telephone or email is configured.', setupLabel: 'Setup → Quote Format', setupPath: '/setup/quote-format' },
      ],
    });
    renderQuote();

    expect(await screen.findByText(/no company address/i)).toBeVisible();
    expect(screen.getByRole('button', { name: /^send to customer$/i })).toBeDisabled();
  });
});
