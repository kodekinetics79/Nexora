import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * Pilot audit CP-04 / UX-06: SEC and Aramco bids are answered on the buyer's portal, so the rep
 * needs the PDF. Export PDF died on a toast — "the price source has not been confirmed", or "no
 * validity date" — and nothing on the quote page could fix either. Both now open the step that
 * fixes them, and the download runs again.
 */

const m = vi.hoisted(() => ({
  getById: vi.fn(), getPriceAttestation: vi.fn(), getSendReadiness: vi.fn(), downloadPdf: vi.fn(),
  confirmPriceAttestation: vi.fn(), saveQuoteTerms: vi.fn(),
}));
const toastMock = vi.hoisted(() => Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }));

vi.mock('../../../api/services/quoteService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/quoteService')>();
  return {
    ...actual,
    default: {
      getById: m.getById,
      getPriceAttestation: m.getPriceAttestation,
      getSendReadiness: m.getSendReadiness,
      downloadPdf: m.downloadPdf,
      confirmPriceAttestation: m.confirmPriceAttestation,
      resolveRevisionImpact: vi.fn(),
      applyRevisionQuantities: vi.fn(),
      sendEmail: vi.fn(),
      getRevisionInfo: vi.fn().mockResolvedValue(null),
    },
  };
});
vi.mock('../../../api/services/rfqService', () => ({ default: { saveQuoteTerms: m.saveQuoteTerms } }));
vi.mock('../../../api/services/currencyService', () => ({
  default: { getAll: vi.fn().mockResolvedValue({ items: [{ id: 3, code: 'SAR', isBaseCurrency: true }] }) },
}));
vi.mock('../../../api/services/procurementService', () => ({
  default: { getWorkbench: vi.fn().mockResolvedValue(null) },
}));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7 }, hasPermission: () => true }),
}));
vi.mock('react-hot-toast', () => ({ toast: toastMock, default: toastMock }));
vi.mock('../../../components/common/CommercialLineIntelligence', () => ({ default: () => null }));
vi.mock('./QuoteOutcomeDialog', () => ({ default: () => null }));
vi.mock('./ExtendValidityDialog', () => ({ default: () => null }));
vi.mock('./customer-awards', () => ({ CustomerAwardDialog: () => null }));
vi.mock('../../../components/common/EmailPromptDialog', () => ({ default: () => null }));
// The real dialog loads the attestation lines; the page's job is to open it for the PDF and act on it.
vi.mock('./PriceConfirmationDialog', () => ({
  default: ({ open, purpose, onConfirm }: { open: boolean; purpose?: string; onConfirm: (s: string, r: string) => void }) =>
    open ? (
      <div role="dialog" aria-label={`Confirm prices (${purpose ?? 'send'})`}>
        <button onClick={() => onConfirm('SUPPLIER_QUOTE', 'SQ-4471')}>Confirm prices</button>
      </div>
    ) : null,
}));

import QuoteViewPage from './QuoteViewPage';

const draft = (overrides = {}) => ({
  id: 13, quoteNo: 'QT-0926-0013', statusValue: 'Draft', statusCode: 'DRAFT', lifecycleVersion: 1,
  currencyId: 3, currencyCode: 'SAR', totalAmount: 1150, quoteDate: '2026-09-28', validUntil: '2026-12-29',
  customerName: 'Saudi Electricity Company', sourceLeadRevision: 1, sourceRfqRevision: 1,
  quoteItems: [{ id: 1, productName: 'Battery', quantity: 10, unitPrice: 100, discount: 0, totalAmount: 1150, taxAmount: 150, taxableBase: 1000, taxRatePercentApplied: 15 }],
  ...overrides,
});
const conflict = (data: Record<string, unknown>) => Object.assign(new Error('409'), { response: { status: 409, data } });

function renderQuote() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/view/13']}>
        <Routes>
          <Route path="/sales/quotes/view/:id" element={<QuoteViewPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  m.getById.mockResolvedValue(draft());
  m.getPriceAttestation.mockResolvedValue({ satisfied: false });
  m.getSendReadiness.mockResolvedValue({ quoteId: 13, canSend: false, blockers: [{ code: 'PRICE_ATTESTATION_REQUIRED', message: 'confirm' }] });
  m.confirmPriceAttestation.mockResolvedValue({ satisfied: true });
  m.saveQuoteTerms.mockResolvedValue({ quoteId: 13 });
  URL.createObjectURL = vi.fn(() => 'blob:quote');
  URL.revokeObjectURL = vi.fn();
});

describe('QuoteViewPage — Export PDF for a portal quote', () => {
  it('opens the price confirmation on a 409 and downloads once the prices are confirmed', async () => {
    m.downloadPdf
      .mockRejectedValueOnce(conflict({ priceAttestationRequired: true, message: 'The price source has not been confirmed.' }))
      .mockResolvedValue(new Blob(['%PDF']));
    renderQuote();

    fireEvent.click(await screen.findByRole('button', { name: /export pdf/i }));

    const confirm = await screen.findByRole('dialog', { name: 'Confirm prices (pdf)' });
    expect(toastMock.error).not.toHaveBeenCalled();
    fireEvent.click(within(confirm).getByRole('button', { name: 'Confirm prices' }));

    await waitFor(() => expect(m.confirmPriceAttestation).toHaveBeenCalledWith(13, 'SUPPLIER_QUOTE', 'SQ-4471'));
    await waitFor(() => expect(m.downloadPdf).toHaveBeenCalledTimes(2));
    expect(URL.createObjectURL).toHaveBeenCalled();
    expect(toastMock.error).not.toHaveBeenCalled();
  });

  it('a missing validity date opens the date, not a toast, and then downloads', async () => {
    m.getById.mockResolvedValue(draft({ validUntil: null }));
    m.getSendReadiness.mockResolvedValue({ quoteId: 13, canSend: false, blockers: [],
      buyerTerms: { requiredValidUntil: '2099-03-01T00:00:00', allowedCurrencies: ['SAR'] } });
    m.downloadPdf
      .mockRejectedValueOnce(conflict({ commercialReviewRequired: true, message: 'Commercial Review Required: this quote has no validity date.' }))
      .mockResolvedValue(new Blob(['%PDF']));
    renderQuote();

    fireEvent.click(await screen.findByRole('button', { name: /export pdf/i }));

    const terms = await screen.findByRole('dialog', { name: 'Set how long the prices hold' });
    expect(toastMock.error).not.toHaveBeenCalled();
    // Defaults to the date the buyer asks for.
    expect(within(terms).getByLabelText('Prices valid until')).toHaveValue('2099-03-01');
    fireEvent.click(within(terms).getByRole('button', { name: 'Save and download' }));

    await waitFor(() => expect(m.saveQuoteTerms).toHaveBeenCalledWith(13, { currencyId: null, validUntil: '2099-03-01' }));
    await waitFor(() => expect(m.downloadPdf).toHaveBeenCalledTimes(2));
  });

  it('any other refusal is still said in the server\'s words', async () => {
    m.downloadPdf.mockRejectedValue(conflict({ commercialReviewRequired: true, message: 'one or more lines have no price.' }));
    renderQuote();

    fireEvent.click(await screen.findByRole('button', { name: /export pdf/i }));

    await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});

describe('QuoteViewPage — buyer warnings before send (inform, never block)', () => {
  const warned = {
    quoteId: 13, canSend: true, blockers: [],
    warnings: [
      { code: 'BUYER_REVISION_NEWER', message: 'The buyer sent a newer version (rev 4): 1 line changed. Quantities: line 2 20 → 35.', canApply: true,
        revision: { impactId: 0, impactType: 'BUYER_REVISION_NEWER', fromRevision: 3, toRevision: 4, changes: [{ line: '2', field: 'quantity', from: '20', to: '35' }] } },
      { code: 'VALIDITY_BELOW_BUYER_MINIMUM', message: 'Buyer asks for prices valid 90 days from closing (until 29 Dec 2026). This quote is valid until 17 Oct 2026.',
        suggestedValidUntil: '2026-12-29T00:00:00' },
    ],
  };

  it('D-01: a quote built after the buyer revision says so, and Send asks before it goes', async () => {
    m.getSendReadiness.mockResolvedValue(warned);
    renderQuote();

    expect(await screen.findByText(/The buyer sent a newer version \(rev 4\)\. Apply the new quantities, or keep it as quoted\./)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /apply the new quantities/i })).toBeInTheDocument();
    expect(screen.getByText(/Buyer asks for prices valid 90 days from closing/)).toBeInTheDocument();

    const send = screen.getAllByRole('button', { name: /send to customer/i })[0];
    expect(send).toBeEnabled();
    fireEvent.click(send);
    const gate = await screen.findByRole('dialog', { name: 'The buyer sent a newer version' });
    expect(within(gate).getByRole('button', { name: 'Send anyway' })).toBeDisabled();
    expect(within(gate).getByRole('button', { name: 'Use the new quantities' })).toBeInTheDocument();
  });

  it("one click sets the validity the buyer asks for", async () => {
    m.getSendReadiness.mockResolvedValue(warned);
    renderQuote();

    fireEvent.click(await screen.findByRole('button', { name: 'Set to 29 Dec 2026' }));

    await waitFor(() => expect(m.saveQuoteTerms).toHaveBeenCalledWith(13, { validUntil: '2026-12-29' }));
  });
});
