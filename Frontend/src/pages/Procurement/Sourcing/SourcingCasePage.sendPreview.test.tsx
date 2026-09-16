import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * The owner's shape: "a email window will open with default content but with the requirement in
 * it and the user will review before sending." The Send dialog listed the ticked suppliers and a
 * deadline but never showed the email or let the rep write in it. Now it previews what each
 * supplier receives (part, maker, quantity, respond-by) with an editable message, and the message
 * travels in the prepare request for every ticked supplier.
 */

const getSourcingCase = vi.fn();
const post = vi.fn();

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useParams: () => ({ caseId: '3' }), useNavigate: () => vi.fn() };
});
vi.mock('../../../api/axiosInstance', () => ({
  default: { post: (...args: unknown[]) => post(...args), get: vi.fn() },
}));
vi.mock('../../../api/services/procurementService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/procurementService')>();
  return {
    ...actual,
    default: {
      ...actual.default,
      getSourcingCase: () => getSourcingCase(),
      // Two known suppliers is fewer than ten, so the page also asks the internet; keep that quiet here.
      discoverSuppliers: () => Promise.resolve({
        status: 'NotConfigured', message: 'Internet search is not available.', total: 0, offset: 0, limit: 10,
        fromCache: false, searchedAtUtc: '2026-09-16T00:00:00Z', hits: [],
        searchedFor: { maker: 'Schneider Electric', partNumber: 'LV431831', description: 'Circuit breaker', acceptableMakers: [] },
      }),
    },
  };
});
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7, userName: 'Rana' }, hasPermission: () => true }),
}));
vi.mock('react-hot-toast', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const SourcingCasePage = (await import('./SourcingCasePage')).default;

const ready = (id: number, supplierName: string) => ({
  id, supplierId: id, supplierName, contactEmail: `sales@${id}.example`, rank: id, evidenceType: 'SUPPLIER_METADATA',
  recommendationReason: 'Tags name LV431831', evidenceScore: 0.6, evidenceFreshOn: null, selected: false,
  governanceStatus: 'APPROVED', readinessStatus: 'READY', eligibleForSupplierRfq: true, blockingReasons: [],
});

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter initialEntries={['/procurement/sourcing-cases/3']}><SourcingCasePage /></MemoryRouter>
  </QueryClientProvider>,
);

const PREPARE_URL = '/api/procurement/sourcing-cases/3/supplier-rfqs';
const prepareBodies = () => post.mock.calls.filter(([url]) => url === PREPARE_URL).map(([, body]) => body);

const openSendDialog = async (...supplierNames: RegExp[]) => {
  renderPage();
  await screen.findByTestId('sourcing-case-next-step');
  for (const name of supplierNames) fireEvent.click(screen.getByRole('checkbox', { name }));
  fireEvent.click(screen.getByRole('button', { name: new RegExp(`^ask ${supplierNames.length} supplier`, 'i') }));
  return screen.findByRole('dialog');
};

beforeEach(() => {
  vi.clearAllMocks();
  getSourcingCase.mockResolvedValue({
    id: 3, commercialDemandLineId: 1, rfqId: 5, rfqItemId: 10, nexoraSerial: 'NX-1', requestedPartNumber: 'LV431831',
    manufacturer: 'Schneider Electric', description: 'Circuit breaker', unitOfMeasure: 'EA',
    requestedQuantity: 13, stockQuantity: 5, unfulfilledQuantity: 8, requiredOn: '2026-10-01T00:00:00',
    searchLimit: 10, status: 'CANDIDATES_READY', nextAction: 'Select suppliers for outreach', version: 1,
    candidates: [ready(1, 'Gulf Switchgear Trading Co.'), ready(2, 'Riyadh Breakers LLC')],
  });
  post.mockImplementation(async (url: string) => ({
    data: url.endsWith('/queue')
      ? { sourcingCaseId: 3, supplierSolicitationId: 9, status: 'Queued', sourcingCaseVersion: 2, solicitationVersion: 1, replayed: false }
      : { sourcingCaseId: 3, supplierSolicitationId: 9, status: 'PendingDispatch', sourcingCaseVersion: 2, solicitationVersion: 1, replayed: false },
  }));
});

describe('SourcingCasePage — the Send dialog shows the email and lets the rep write in it', () => {
  it('previews the request as the supplier reads it, with the standard message ready to edit', async () => {
    const dialog = await openSendDialog(/select gulf switchgear/i);

    const preview = within(dialog).getByTestId('supplier-rfq-preview');
    expect(preview).toHaveTextContent('Dear <supplier name>,');
    expect(preview).toHaveTextContent('LV431831');
    expect(preview).toHaveTextContent('Schneider Electric');
    expect(preview).toHaveTextContent('8 EA (the rest of the 13 EA comes from stock)');
    expect(preview).toHaveTextContent('Needed by2026-10-01');
    expect(preview).toHaveTextContent('Respond by: Please respond promptly');
    expect(within(dialog).getByLabelText('Your message to the suppliers')).toHaveValue('Please submit your best pricing and lead times.');
    expect(dialog).toHaveTextContent('Your customer is not named');
  });

  it('sends the standard message when the rep leaves it untouched', async () => {
    const dialog = await openSendDialog(/select gulf switchgear/i);

    fireEvent.click(within(dialog).getByRole('button', { name: /^send 1 rfq$/i }));

    await waitFor(() => expect(prepareBodies()).toHaveLength(1));
    expect(prepareBodies()[0]).toEqual({
      supplierId: 1, expectedVersion: 1, dueOn: null, message: 'Please submit your best pricing and lead times.',
    });
  });

  it('sends what the rep wrote, the same words to every ticked supplier', async () => {
    const dialog = await openSendDialog(/select gulf switchgear/i, /select riyadh breakers/i);

    fireEvent.change(within(dialog).getByLabelText('Your message to the suppliers'), {
      target: { value: '  Quote DDP Jubail and include HS codes.  ' },
    });
    fireEvent.click(within(dialog).getByRole('button', { name: /^send 2 rfqs$/i }));

    await waitFor(() => expect(prepareBodies()).toHaveLength(2));
    expect(prepareBodies().map((body) => [body.supplierId, body.message])).toEqual([
      [1, 'Quote DDP Jubail and include HS codes.'],
      [2, 'Quote DDP Jubail and include HS codes.'],
    ]);
  });

  it('a cleared message is sent as none so the email keeps its standard sentence', async () => {
    const dialog = await openSendDialog(/select gulf switchgear/i);

    fireEvent.change(within(dialog).getByLabelText('Your message to the suppliers'), { target: { value: '   ' } });
    expect(dialog).toHaveTextContent('Leave it blank and the email says');
    fireEvent.click(within(dialog).getByRole('button', { name: /^send 1 rfq$/i }));

    await waitFor(() => expect(prepareBodies()).toHaveLength(1));
    expect(prepareBodies()[0].message).toBeNull();
  });
});
