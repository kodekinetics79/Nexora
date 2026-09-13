import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import ExtractionReviewDetailPage from './ExtractionReviewDetailPage';

/**
 * Corrections in progress must survive a re-read of the same review version. The seeding effect ran
 * on ANY change to the lead payload — a reconnect, another screen invalidating the lead — and reset
 * the header, the lines, the staged client and the grid selection.
 */

vi.setConfig({ testTimeout: 30_000 });

const getColumns = vi.fn();
const saveColumns = vi.fn();
const resetColumns = vi.fn();

vi.mock('../../api/services/listViewService', () => ({
  default: {
    getColumns: (viewKey: string) => getColumns(viewKey),
    saveColumns: (viewKey: string, columns: unknown) => saveColumns(viewKey, columns),
    resetColumns: (viewKey: string) => resetColumns(viewKey),
  },
}));

const getLeadLineResolutions = vi.fn();
vi.mock('../../api/services/commercialIntelligenceService', () => ({
  default: {
    getLeadLineResolutions: (leadId: number) => getLeadLineResolutions(leadId),
  },
}));

const getLead = vi.fn();
vi.mock('../../api/services/extractionReviewService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/extractionReviewService')>();
  return {
    ...actual,
    default: {
      getLead: (id: number) => getLead(id),
      getProcessingEvidence: () => Promise.resolve(null),
      getFieldEvidence: () => Promise.resolve({ entries: [], mapped: false, documentNarrative: '' }),
      submitReview: () => Promise.resolve({}),
    },
  };
});

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => vi.fn(), useParams: () => ({ id: '7' }) };
});

vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));

// Two lines sharing nothing, plus one duplicate part, so every join outcome is covered.
const LEAD = {
  id: 7,
  rfqno: 'RFQ-7',
  buyersName: 'Buyer',
  bidClosingDate: '2026-09-01T00:00:00Z',
  requiredDeliveryDate: '2026-10-01T00:00:00Z',
  deliveryLocation: 'North Logistics Hub, Gate 4',
  agreementReference: 'FRAME-2026-118',
  opportunityNo: '',
  headerRemarks: '',
  recDate: '2026-08-01T00:00:00Z',
  leadSource: 'EMAIL',
  reviewVersion: 1,
  attachments: [],
  leadItems: [
    {
      id: 101, lineItemNo: '10', productShortName: 'Ball valve',
      manufacturerPartNumber: 'BV-100', quantity: 5, unitOfMeasure: 'EA',
      customFields: '{"plant_code":"JBL-2"}',
    },
    {
      id: 102, lineItemNo: '20', productShortName: 'Gasket',
      manufacturerPartNumber: 'GK-9', quantity: 12, unitOfMeasure: 'EA',
      // No custom-field bag at all: the column must still render an explicit state.
      customFields: null,
    },
  ],
};

const flushQueryNotifications = () => act(async () => {
  await new Promise((resolve) => setTimeout(resolve, 20));
});

const mount = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <ExtractionReviewDetailPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return client;
};

beforeEach(() => {
  vi.clearAllMocks();
  sessionStorage.clear();
  getColumns.mockResolvedValue({ viewKey: 'lead.items', isCustomised: false, supportsCustomFields: false, columns: [] });
  getLeadLineResolutions.mockResolvedValue([]);
});

describe('ExtractionReviewDetailPage — the lead is read again during review', () => {
  it('keeps typed corrections when the same review version comes back changed', async () => {
    getLead.mockResolvedValueOnce(LEAD).mockResolvedValue({ ...LEAD, buyersName: 'Buyer (renamed elsewhere)' });
    const client = mount();

    const remarks = await screen.findByLabelText('Remarks');
    fireEvent.change(remarks, { target: { value: 'Line 20 quantity confirmed by phone' } });

    await act(async () => {
      await client.refetchQueries({ queryKey: ['needs-review-detail', 7] });
    });
    await flushQueryNotifications();

    expect(getLead).toHaveBeenCalledTimes(2);
    expect(screen.getByLabelText('Remarks')).toHaveValue('Line 20 quantity confirmed by phone');
  });

  it('takes the server copy when a NEW review version arrives and nothing was typed (the control)', async () => {
    getLead.mockResolvedValueOnce(LEAD).mockResolvedValue({ ...LEAD, reviewVersion: 2, headerRemarks: 'Saved by another reviewer' });
    const client = mount();

    expect(await screen.findByLabelText('Remarks')).toHaveValue('');

    await act(async () => {
      await client.refetchQueries({ queryKey: ['needs-review-detail', 7] });
    });

    expect(await screen.findByDisplayValue('Saved by another reviewer')).toBeInTheDocument();
  });
});
