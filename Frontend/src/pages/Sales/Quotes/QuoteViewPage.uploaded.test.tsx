import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * A quote the rep made outside Nexora and uploaded (owner request 2026-09-28). The customer holds
 * the rep's own file, so the page downloads that file, never sends or revises it, and shows no
 * send checks or "cost source pending" for a quote Nexora never priced.
 */

const { getById, getPriceAttestation, getSendReadiness, downloadUploadedFile, downloadPdf } = vi.hoisted(() => ({
  getById: vi.fn(),
  getPriceAttestation: vi.fn(),
  getSendReadiness: vi.fn(),
  downloadUploadedFile: vi.fn(),
  downloadPdf: vi.fn(),
}));

vi.mock('../../../api/services/quoteService', () => ({
  default: {
    getById,
    getPriceAttestation,
    getSendReadiness,
    sendEmail: vi.fn(),
    transitionStatus: vi.fn(),
    exportPdf: vi.fn(),
    downloadPdf,
    downloadUploadedFile,
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

const uploadedQuote = {
  id: 14, quoteNo: 'QT-0926-0007', statusValue: 'Sent', statusCode: 'SENT', lifecycleVersion: 2,
  currencyId: 1, currencyCode: 'SAR', totalAmount: 14375, quoteDate: '2026-09-28', validUntil: '2026-10-28',
  sentOn: '2026-09-28T09:00:00', customerName: 'Saudi Electricity Company', rfqId: 3,
  externalQuoteReference: 'QT-EXCEL-2291', uploadedFileName: 'QT-EXCEL-2291.pdf',
  quoteItems: [
    { id: 30, itemDescription: 'As per quote QT-EXCEL-2291 (uploaded file)', quantity: 1, unitOfMeasure: 'LOT', unitPrice: 12500, discount: 0, totalAmount: 14375, taxAmount: 1875, taxableBase: 12500, taxRatePercentApplied: 15 },
  ],
};

function renderQuote() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/view/14']}>
        <Routes><Route path="/sales/quotes/view/:id" element={<QuoteViewPage />} /></Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getById.mockResolvedValue(uploadedQuote);
  getPriceAttestation.mockResolvedValue({ satisfied: false });
  // What the readiness check says of any quote with no confirmed price source.
  getSendReadiness.mockResolvedValue({
    quoteId: 14, canSend: false,
    blockers: [{ code: 'PRICE_ATTESTATION_REQUIRED', message: 'The price source has not been confirmed for this quote.' }],
  });
  downloadUploadedFile.mockResolvedValue(undefined);
});

describe('QuoteViewPage — a quote made outside Nexora and uploaded', () => {
  it('downloads the uploaded file instead of exporting a Nexora PDF', async () => {
    renderQuote();

    fireEvent.click(await screen.findByRole('button', { name: 'Download quote' }));

    expect(downloadUploadedFile).toHaveBeenCalledWith(14, 'QT-EXCEL-2291.pdf');
    expect(downloadPdf).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Export PDF' })).not.toBeInTheDocument();
  });

  it('is not sent again from Nexora and shows no send checks or pending cost', async () => {
    renderQuote();

    expect(await screen.findByText('As per quote QT-EXCEL-2291 (uploaded file)')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /send again/i })).not.toBeInTheDocument();
    // Send again lives under More on a Nexora quote: open it, so the absence is real.
    fireEvent.click(screen.getByRole('button', { name: /^more$/i }));
    expect(await screen.findByRole('menuitem', { name: /follow up on this quote/i })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: /send again/i })).not.toBeInTheDocument();
    expect(screen.queryByText(/price source has not been confirmed/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/cost source pending|no cost yet/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: 'Cost source' })).not.toBeInTheDocument();
  });

  it('is neither edited nor revised here, says the number on the file, and still records what happened', async () => {
    renderQuote();

    // The one next step on a quote with the customer.
    const update = await screen.findByRole('button', { name: 'Update status' });
    expect(update).toHaveClass('MuiButton-contained');
    expect(screen.queryByRole('button', { name: /^edit$/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /make a revision/i })).not.toBeInTheDocument();
    expect(screen.getByText('No. on the file')).toBeInTheDocument();
    expect(screen.getByText('QT-EXCEL-2291')).toBeInTheDocument();
    expect(screen.getByText('Uploaded')).toBeInTheDocument();
  });

  it('with a buyer revision, names only what is on the screen: update the status, no "keep as quoted"', async () => {
    getById.mockResolvedValue({ ...uploadedQuote, revisionImpact: 'DRAFT_STALE_REVIEW_REQUIRED', revisionImpactDetail: null });
    renderQuote();

    const panel = await screen.findByTestId('quote-next-step');
    await waitFor(() => expect(panel).toHaveTextContent('The buyer sent a newer version after this quote went out. Update the status when they decide.'));
    expect(panel).not.toHaveTextContent(/keep it as quoted/i);
    // The control the sentence names is here, and the one it no longer names is not.
    expect(screen.getByRole('button', { name: 'Update status' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /keep as quoted/i })).not.toBeInTheDocument();
  });
});
