import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import StockLevelsPage from './StockLevelsPage';
import ReorderAlertsPage from './ReorderAlertsPage';
import type {
  ReorderAlertsDTO, StockLevelsDTO,
} from '../../../api/services/commercialIntelligenceService';

/**
 * FR-INV-04. What is locked down here is what makes these two screens truthful rather than merely
 * populated:
 *  - a level that has never been set renders as "Not set", never as an empty numeric cell that
 *    reads as zero — an unset minimum is the reason an empty warehouse raises no alert;
 *  - the count of rows nobody is watching is on the wall, because an empty alerts screen and a
 *    healthy warehouse look identical without it;
 *  - an alert nobody was emailed says so, rather than leaving a blank that reads like "sent";
 *  - an acknowledgement cannot be submitted without a reason.
 */

const getStockLevels = vi.fn();
const getReorderAlerts = vi.fn();
const acknowledgeReorderAlert = vi.fn();
const getWarehouses = vi.fn();

vi.mock('../../../api/services/commercialIntelligenceService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/commercialIntelligenceService')>();
  return {
    ...actual,
    default: {
      getStockLevels: (breachedOnly: boolean) => getStockLevels(breachedOnly),
      getReorderAlerts: (params: unknown) => getReorderAlerts(params),
      acknowledgeReorderAlert: (id: number, version: number, reason: string) =>
        acknowledgeReorderAlert(id, version, reason),
      getWarehouses: () => getWarehouses(),
    },
  };
});

const hasPermission = vi.fn();
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: (module: string, action?: string) => hasPermission(module, action) }),
}));

/** Two rows: one configured and short, one that nobody has ever set a level on. */
const LEVELS: StockLevelsDTO = {
  generatedAt: '2026-08-10T09:00:00Z',
  rowCount: 2,
  configuredCount: 1,
  breachedCount: 1,
  unmonitoredCount: 1,
  rows: [
    {
      inventoryId: 1, productId: 11, partNumber: 'VLV-100', productName: 'Gate valve',
      warehouseId: 21, warehouseName: 'Dammam', onHand: 4, available: 2, incoming: 0, projected: 2,
      minimumLevel: 10, maximumLevel: 60, reorderPoint: 15,
      breach: 'BELOW_MINIMUM', thresholdQuantity: 10, shortfallQuantity: 8, monitored: true,
    },
    {
      inventoryId: 2, productId: 12, partNumber: 'GSK-200', productName: 'Spiral gasket',
      warehouseId: 21, warehouseName: 'Dammam', onHand: 0, available: 0, incoming: 0, projected: 0,
      minimumLevel: null, maximumLevel: null, reorderPoint: 0,
      breach: null, thresholdQuantity: 0, shortfallQuantity: 0, monitored: false,
    },
  ],
};

const ALERTS: ReorderAlertsDTO = {
  generatedAt: '2026-08-10T09:00:00Z',
  alertCount: 1,
  rows: [
    {
      id: 501, inventoryId: 1, productId: 11, warehouseId: 21,
      partNumber: 'VLV-100', productName: 'Gate valve', warehouseName: 'Dammam',
      kind: 'BELOW_MINIMUM', status: 'OPEN', severity: 'critical',
      onHandQuantity: 4, availableQuantity: 2, incomingQuantity: 0, projectedQuantity: 2,
      thresholdQuantity: 10, shortfallQuantity: 8,
      raisedOn: '2026-08-10T08:30:00Z',
      // The provider accepted nothing. The alert is live and nobody has been told.
      notifiedCount: 0,
      acknowledgedOn: null, acknowledgedBy: null, acknowledgementReason: null,
      resolvedOn: null, resolutionReason: null, version: 1,
    },
  ],
};

function renderPage(node: React.ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>{node}</SnackbarProvider>
    </QueryClientProvider>,
  );
}

describe('Stock levels', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    hasPermission.mockReturnValue(true);
    getStockLevels.mockResolvedValue(LEVELS);
    getWarehouses.mockResolvedValue([]);
  });

  it('renders a level that was never configured as "Not set" rather than as an empty cell', async () => {
    renderPage(<StockLevelsPage />);

    await screen.findByText(/GSK-200/);
    // Three unset numbers on that row: minimum, reorder point, maximum. A blank numeric cell would
    // read as zero, and zero would mean "somebody decided nothing is enough".
    expect(screen.getAllByText('Not set').length).toBeGreaterThanOrEqual(3);
    expect(screen.getByText('Not monitored')).toBeInTheDocument();
  });

  it('states how many rows nobody is watching, because an empty alerts screen looks the same as a healthy one', async () => {
    renderPage(<StockLevelsPage />);

    await waitFor(() => expect(
      screen.getByText(/1 of 2 stock rows have no minimum, maximum or reorder point set/),
    ).toBeInTheDocument());
  });
});

describe('Reorder alerts', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    hasPermission.mockReturnValue(true);
    getReorderAlerts.mockResolvedValue(ALERTS);
    acknowledgeReorderAlert.mockResolvedValue(undefined);
  });

  it('says so when no email copy was accepted, rather than leaving a blank that reads like "sent"', async () => {
    renderPage(<ReorderAlertsPage />);

    await screen.findByText(/VLV-100/);
    expect(screen.getByText('Not emailed')).toBeInTheDocument();
  });

  it('refuses to submit an acknowledgement with no reason', async () => {
    renderPage(<ReorderAlertsPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Acknowledge' }));
    const submit = await screen.findByRole('button', { name: 'Acknowledge' });
    expect(submit).toBeDisabled();
    expect(acknowledgeReorderAlert).not.toHaveBeenCalled();
  });

  it('sends the reason verbatim with the alert version, so a concurrent change is caught', async () => {
    renderPage(<ReorderAlertsPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Acknowledge' }));
    fireEvent.change(await screen.findByLabelText(/What are you doing about it/), {
      target: { value: 'PO 4417 raised with the mill.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Acknowledge' }));

    await waitFor(() => expect(acknowledgeReorderAlert)
      .toHaveBeenCalledWith(501, 1, 'PO 4417 raised with the mill.'));
  });
});
