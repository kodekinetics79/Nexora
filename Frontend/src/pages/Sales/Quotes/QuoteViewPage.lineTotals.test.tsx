import { render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * The quote screen's lines say what the customer's PDF says (pilot audit UX-04, CB-13, UX-03).
 *
 * View Quote printed the stored `totalAmount` in the line column — 2,876.15, VAT included — where
 * the PDF, the price window and Edit Quote all show 2,501.00. Its lines did not add up to its own
 * "Gross Subtotal", and a rep reading the line out to a buyer quoted the wrong price. It also
 * showed "Pricing Pending" for a line the PDF calls "Not quoted", no material / maker / part
 * number, and the internal "Commercial Review Required" marker as if it were a remark.
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

const quote = (overrides: Record<string, unknown> = {}) => ({
  id: 14,
  quoteNo: 'QT-0928-0014',
  statusValue: 'Draft',
  statusCode: 'DRAFT',
  lifecycleVersion: 1,
  currencyId: 1,
  currencyCode: 'SAR',
  totalAmount: 10696.15,
  quoteDate: '2026-09-28',
  validUntil: '2026-12-27',
  customerName: 'Saudi Electricity Company',
  headerRemarks: 'Commercial Review Required: pricing, inventory, lead time, tax, freight and validity remain pending.',
  quoteItems: [
    {
      id: 56, customerLineRef: '10', productName: 'RTD sensor', itemDescription: 'RTD PT100 3-wire',
      quantity: 2, unitOfMeasure: 'EA', unitPrice: 1250.5, discount: 0,
      totalAmount: 2876.15, taxAmount: 375.15, taxableBase: 2501, taxRatePercentApplied: 15,
      customerMaterialCode: '902507285', manufacturerName: 'ABB', manufacturerPartNumber: 'PT100',
    },
    {
      id: 57, customerLineRef: '20', productName: 'Detector', itemDescription: 'Gas detector',
      quantity: 1, unitOfMeasure: 'EA', unitPrice: 0, discount: 0,
      totalAmount: 0, taxAmount: 0, taxableBase: 0, taxRatePercentApplied: 0,
      pricingStatus: 'NOT_QUOTED', pricingNote: 'Discontinued by manufacturer',
      requestedItemMaterialCode: '909304445', requestedManufacturerName: 'TELEDYNE',
      requestedManufacturerPartNumber: 'EX-XM10050-009',
    },
  ],
  ...overrides,
});

function renderQuote() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/view/14']}>
        <Routes>
          <Route path="/sales/quotes/view/:id" element={<QuoteViewPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getPriceAttestation.mockResolvedValue({ satisfied: true });
  getById.mockResolvedValue(quote());
});

describe('the quoted lines on View Quote', () => {
  it('show the ex-VAT line total the PDF prints, never the VAT-inclusive stored total', async () => {
    renderQuote();
    const row = (await screen.findByText('RTD PT100 3-wire')).closest('tr') as HTMLElement;

    expect(within(row).getByText(/2,501\.00/)).toBeInTheDocument();
    expect(row).not.toHaveTextContent('2,876.15');
    expect(screen.getByRole('columnheader', { name: 'Total excl. VAT' })).toBeInTheDocument();
  });

  it('show the buyer material, maker and part number, stored or requested', async () => {
    renderQuote();

    expect(await screen.findByText('Material: 902507285 · Make: ABB · Part no.: PT100')).toBeInTheDocument();
    expect(screen.getByText('Material: 909304445 · Make: TELEDYNE · Part no.: EX-XM10050-009')).toBeInTheDocument();
  });

  it('say "Not quoted" with the reason, as the PDF does, instead of "Pricing Pending"', async () => {
    renderQuote();
    const row = (await screen.findByText('Gas detector')).closest('tr') as HTMLElement;

    expect(within(row).getByText('Not quoted: Discontinued by manufacturer')).toBeInTheDocument();
    expect(within(row).getByText('Not quoted')).toBeInTheDocument();
    expect(within(row).queryByText('Pricing Pending')).not.toBeInTheDocument();
  });

  it('do not present the internal draft marker as a note to the customer', async () => {
    renderQuote();
    await screen.findByText('RTD PT100 3-wire');

    expect(screen.queryByText(/Commercial Review Required: pricing, inventory/)).not.toBeInTheDocument();
  });

  it('show the rep\'s own notes as the notes the customer receives', async () => {
    getById.mockResolvedValue(quote({ headerRemarks: 'Delivery within 4 weeks, DAP Dammam' }));
    renderQuote();

    expect(await screen.findByText('Delivery within 4 weeks, DAP Dammam')).toBeInTheDocument();
    expect(screen.getByText('NOTES TO CUSTOMER')).toBeInTheDocument();
  });
});
