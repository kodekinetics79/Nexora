import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * The tax gate on "Send to customer".
 *
 * `summariseStoredQuote` reports `hasUnderivedTax` when any line has no calculated tax — the
 * tenant has not set an output VAT rate yet. The Financial Summary rendered a warning saying
 * the quote "cannot be sent", but the send button only looked at `isUnpricedDraft` and
 * `revisionImpact`, so it stayed enabled. The rep saw a contradiction, clicked, chose a
 * recipient, confirmed the prices and was then refused by the server. The same panel printed
 * "Total excluding VAT 0.00" beside a real subtotal, because the taxable base is never stored
 * for a line whose tax was never derived.
 *
 * These tests pin that the button, the reason beside it, and the ex-VAT line all agree.
 */

const { getById, getPriceAttestation } = vi.hoisted(() => ({
  getById: vi.fn(),
  getPriceAttestation: vi.fn(),
}));

vi.mock('../../../api/services/quoteService', () => ({
  default: {
    getById,
    getPriceAttestation,
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
  useAuth: () => ({
    userData: { businessUnitId: 1 },
    hasPermission: () => true,
  }),
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

const pricedDraft = (line: Record<string, unknown>) => ({
  id: 9,
  quoteNo: 'QT-2026-0009',
  statusValue: 'Draft',
  statusCode: 'DRAFT',
  lifecycleVersion: 1,
  currencyId: 1,
  currencyCode: 'SAR',
  totalAmount: 100,
  quoteDate: '2026-08-01',
  validUntil: '2026-09-01',
  quoteItems: [{
    id: 1, productName: 'Cable tray', quantity: 1, unitPrice: 100, discount: 0, totalAmount: 100,
    ...line,
  }],
  customerName: 'Aramco',
});

function renderQuote() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/view/9']}>
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
});

describe('a priced draft whose tax was never derived', () => {
  beforeEach(() => {
    getById.mockResolvedValue(pricedDraft({ taxAmount: null, taxableBase: null, taxRatePercentApplied: null }));
  });

  it('disables Send to customer and prints the fix beside it, with a link to Commercial Policy', async () => {
    renderQuote();

    const send = await screen.findByRole('button', { name: /send to customer/i });
    expect(send).toBeDisabled();
    expect(screen.getByText(/set the vat rate in setup > commercial policy before sending/i)).toBeVisible();
    expect(screen.getByRole('link', { name: /open commercial policy/i })).toHaveAttribute('href', '/setup/commercial-policy');
  });

  it('shows a dash for Total excluding VAT instead of a false 0.00', async () => {
    renderQuote();
    await screen.findByRole('button', { name: /send to customer/i });

    const row = screen.getByText('Total excluding VAT').parentElement as HTMLElement;
    expect(row).toHaveTextContent('—');
    expect(row).not.toHaveTextContent('0.00');
  });
});

describe('a priced draft with derived tax', () => {
  it('keeps Send to customer enabled with no reason printed (the control for the tests above)', async () => {
    getById.mockResolvedValue(pricedDraft({ taxAmount: 15, taxableBase: 100, taxRatePercentApplied: 15 }));
    renderQuote();

    const send = await screen.findByRole('button', { name: /send to customer/i });
    expect(send).toBeEnabled();
    expect(screen.queryByText(/set the vat rate/i)).not.toBeInTheDocument();
    const row = screen.getByText('Total excluding VAT').parentElement as HTMLElement;
    expect(row).toHaveTextContent('100.00');
  });
});

describe('an unpriced draft', () => {
  it('says the lines need prices, in the button title and beside it', async () => {
    getById.mockResolvedValue({
      ...pricedDraft({ unitPrice: 0, totalAmount: 0, taxAmount: null }),
      currencyId: null,
      totalAmount: 0,
    });
    renderQuote();

    const send = await screen.findByRole('button', { name: /send to customer/i });
    expect(send).toBeDisabled();
    expect(send).toHaveAttribute('title', expect.stringMatching(/add prices/i));
    expect(screen.getByText(/add prices to the quote lines before sending/i)).toBeVisible();
  });
});
