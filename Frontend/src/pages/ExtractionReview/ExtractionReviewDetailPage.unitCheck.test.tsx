import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import ExtractionReviewDetailPage from './ExtractionReviewDetailPage';

/**
 * A line whose unit is "BANANAS" used to render "Verified: all required fields present". The
 * only question the review screen asked was "is the cell blank"; the question a rep needs
 * answered is "is this one of our units". These tests mount the workbench with the tenant's
 * units mocked and read the status column exactly as a screen reader would.
 */
vi.setConfig({ testTimeout: 30_000 });

vi.mock('../../api/services/listViewService', () => ({
  default: {
    getColumns: () => Promise.resolve({
      viewKey: 'lead.items', isCustomised: false, supportsCustomFields: false,
      columns: [
        { key: 'checkStatus', label: 'Review status', visible: true, locked: true, source: 'catalog' },
        { key: 'lineItemNo', label: 'Line #', visible: true, locked: false, source: 'catalog' },
        { key: 'productShortName', label: 'Product', visible: true, locked: false, source: 'catalog' },
        { key: 'unitOfMeasure', label: 'UoM', visible: true, locked: false, source: 'catalog' },
        { key: 'actions', label: 'Actions', visible: true, locked: true, source: 'catalog' },
      ],
    }),
    saveColumns: vi.fn(),
    resetColumns: vi.fn(),
  },
}));

vi.mock('../../api/services/commercialIntelligenceService', () => ({
  default: { getLeadLineResolutions: () => Promise.resolve([]) },
}));

const listForTenant = vi.fn();
vi.mock('../../api/services/uomService', () => ({
  default: { listForTenant: () => listForTenant() },
}));

vi.mock('../../api/services/extractionReviewService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/extractionReviewService')>();
  return {
    ...actual,
    default: {
      getLead: () => Promise.resolve(LEAD),
      getProcessingEvidence: () => Promise.resolve(null),
      getFieldEvidence: () => Promise.resolve({ entries: [], mapped: false, documentNarrative: '' }),
      submitReview: () => Promise.resolve({}),
    },
  };
});

vi.mock('../../context/AuthContext', () => ({ useAuth: () => ({ hasPermission: () => true }) }));
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => vi.fn(), useParams: () => ({ id: '7' }) };
});
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));

const LEAD = {
  id: 7, rfqno: 'RFQ-7', buyersName: 'Buyer', bidClosingDate: '2026-09-01T00:00:00Z', requiredDeliveryDate: null,
  deliveryLocation: '', agreementReference: '', opportunityNo: '', headerRemarks: '', recDate: '2026-08-01T00:00:00Z',
  leadSource: 'EMAIL', reviewVersion: 1, attachments: [],
  leadItems: [
    { id: 101, lineItemNo: '10', productShortName: 'Ball valve', manufacturerPartNumber: 'BV-100', quantity: 5, unitOfMeasure: 'EA' },
    { id: 102, lineItemNo: '20', productShortName: 'Gasket', manufacturerPartNumber: 'GK-9', quantity: 12, unitOfMeasure: 'BANANAS' },
    // The existing rule: a blank quantity is flagged. The unit rule must sit beside it, not replace it.
    { id: 103, lineItemNo: '30', productShortName: 'Bolt', manufacturerPartNumber: 'BT-1', quantity: null, unitOfMeasure: 'EA' },
  ],
};

const renderPage = () =>
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter>
        <ExtractionReviewDetailPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );

/** The status cell's accessible label for one line. */
const statusLabel = async (lineNumber: string): Promise<string> => {
  const cell = await waitFor(() => {
    const cells = document.querySelectorAll('[data-field="checkStatus"] [role="img"]');
    const match = [...cells].find((element) => {
      const row = element.closest('.MuiDataGrid-row');
      return row?.querySelector('[data-field="lineItemNo"]')?.textContent === lineNumber;
    });
    if (!match) throw new Error(`no status cell on line ${lineNumber}`);
    return match as HTMLElement;
  });
  return cell.getAttribute('aria-label') ?? '';
};

beforeEach(() => {
  vi.clearAllMocks();
  listForTenant.mockResolvedValue([
    { uomId: 1, businessUnitId: 1, uomCode: 'EA', uomName: 'Each', description: null, isActive: true },
    { uomId: 2, businessUnitId: 1, uomCode: 'M', uomName: 'Metre', description: null, isActive: true },
  ]);
});

describe('a unit the business does not use needs a check', () => {
  it('flags the BANANAS line, keeps the EA line verified, and still flags the blank quantity', async () => {
    renderPage();
    expect(await statusLabel('20')).toBe("Needs check: Unit 'BANANAS' is not one of your units — pick one");
    expect(await statusLabel('10')).toBe('Verified: all required fields present');
    expect(await statusLabel('30')).toBe('Needs check: Quantity is blank');
    expect(await screen.findByText('2 of 3 lines need a check')).toBeInTheDocument();
  });

  it('does not invent a flag while the tenant units cannot be loaded', async () => {
    listForTenant.mockRejectedValue(new Error('503'));
    renderPage();
    expect(await statusLabel('20')).toBe('Verified: all required fields present');
    expect(await statusLabel('30')).toBe('Needs check: Quantity is blank');
  });
});
