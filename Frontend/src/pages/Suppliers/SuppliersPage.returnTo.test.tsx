import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { GridColDef } from '@mui/x-data-grid';

// A full DataGrid page through jsdom; 5s is not enough on a cold machine.
vi.setConfig({ testTimeout: 30_000 });

/**
 * "Add a supplier" on an empty sourcing case opens this page's form and promises "Saving takes you
 * back to the case." It did — to a case that still said "No supplier on your list is linked to
 * LV431831 yet … press Refresh candidates" until the rep pressed Refresh (found driving the
 * journey on 2026-09-15). The return address now carries the refresh flag the case acts on.
 */

const create = vi.fn();
const deleteSupplier = vi.fn();
const getAll = vi.fn();
const navigate = vi.fn();

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { id: 1, userName: 'Rana', businessUnitId: 0 }, hasPermission: () => true }),
}));
vi.mock('../../api/services/supplierService', () => ({
  default: {
    getAll: (...args: unknown[]) => getAll(...args),
    create: (fd: FormData) => create(fd),
    update: vi.fn(), delete: (id: number) => deleteSupplier(id),
    downloadTemplate: vi.fn(), uploadTemplate: vi.fn(), export: vi.fn(),
  },
  SUPPLIER_TIERS: [
    { value: 'TIER_1_PARTNER', label: 'In Network — partner' },
    { value: 'TIER_2_EXTENDED', label: 'Extended Network — approved supplier' },
    { value: 'TIER_3_OUT_OF_NETWORK', label: 'Outside Network — exception supplier' },
  ],
  supplierTierLabel: () => 'Not classified',
}));
vi.mock('../../api/services/contactService', () => ({
  default: { getBySupplier: vi.fn().mockResolvedValue([]), getAll: vi.fn().mockResolvedValue([]) },
}));
vi.mock('../../api/services/currencyService', () => ({ default: { getAll: vi.fn().mockResolvedValue({ items: [] }) } }));
vi.mock('../../api/services/countryService', () => ({ default: { getAll: vi.fn().mockResolvedValue([]) } }));
vi.mock('../../api/services/cityService', () => ({ default: { getAll: vi.fn().mockResolvedValue([]) } }));
vi.mock('../../hooks/useColumnPreferences', () => ({
  default: () => ({
    columnVisibilityModel: {}, onColumnVisibilityModelChange: vi.fn(),
    arrangeColumns: (defs: GridColDef[]) => defs, isLoading: false, isError: false,
  }),
}));
vi.mock('../../components/common/ColumnPreferences', () => ({ default: () => null }));
vi.mock('../../components/common/UploadExportToolbar', () => ({ default: () => null }));
vi.mock('../../components/common/CustomFieldValuesEditor', () => ({ default: () => null }));

const SuppliersPage = (await import('./SuppliersPage')).default;

const renderAt = (url: string) => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter initialEntries={[url]}><SuppliersPage /></MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  getAll.mockResolvedValue({ items: [], totalCount: 0 });
  create.mockResolvedValue({ id: 9, name: 'Gulf Switchgear Trading Co.' });
  deleteSupplier.mockResolvedValue(undefined);
});

describe('SuppliersPage — supplier profile deletion', () => {
  const supplier = {
    id: 17,
    name: 'Unused Test Supplier',
    contactEmail: 'unused@example.com',
    isActive: true,
    governanceStatus: 'UNVERIFIED',
    verificationStatus: 'UNKNOWN',
    complianceStatus: 'UNKNOWN',
    riskStatus: 'UNKNOWN',
    readinessStatus: 'REVIEW_REQUIRED',
  };

  it('requires an explicit destructive confirmation before deleting an unused profile', async () => {
    getAll.mockResolvedValue({ items: [supplier], totalCount: 1 });
    renderAt('/suppliers');

    fireEvent.click(await screen.findByRole('button', { name: `Delete ${supplier.name}` }));
    expect(screen.getByRole('dialog', { name: /delete unused supplier profile/i })).toBeInTheDocument();
    expect(screen.getByText(/cannot be undone/i)).toBeInTheDocument();
    expect(deleteSupplier).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Delete profile' }));

    await waitFor(() => expect(deleteSupplier).toHaveBeenCalledWith(17));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: /delete unused supplier profile/i })).not.toBeInTheDocument());
  });

  it('keeps the dialog open and explains deactivation when commercial history protects the supplier', async () => {
    getAll.mockResolvedValue({ items: [supplier], totalCount: 1 });
    deleteSupplier.mockRejectedValue({
      response: { data: { detail: 'Supplier records with commercial lineage cannot be deleted.' } },
    });
    renderAt('/suppliers');

    fireEvent.click(await screen.findByRole('button', { name: `Delete ${supplier.name}` }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete profile' }));

    expect(await screen.findByText(/use Supplier Governance to mark it inactive instead/i)).toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: /delete unused supplier profile/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Open profile' })).toBeInTheDocument();
  });
});

describe('SuppliersPage — saving a supplier for a sourcing case', () => {
  it('returns to the case with the refresh flag, so the new supplier is listed without a click', async () => {
    renderAt('/suppliers?new=1&tags=LV431831&returnTo=%2Fprocurement%2Fsourcing-cases%2F3');

    expect(await screen.findByText(/saving takes you back to the case/i)).toBeInTheDocument();
    fireEvent.change(screen.getByRole('textbox', { name: /supplier_name/i }), { target: { value: 'Gulf Switchgear Trading Co.' } });
    fireEvent.mouseDown(screen.getByRole('combobox', { name: /network relationship/i }));
    fireEvent.click(await screen.findByRole('option', { name: /extended network/i }));
    fireEvent.click(screen.getByRole('button', { name: /save_supplier/i }));

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    const payload = create.mock.calls[0][0] as FormData;
    expect(payload.get('tier')).toBe('TIER_2_EXTENDED');
    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/procurement/sourcing-cases/3?refresh=1'));
  });

  it('requires a network relationship before a manually added supplier is saved', async () => {
    renderAt('/suppliers?new=1');

    fireEvent.change(await screen.findByRole('textbox', { name: /supplier_name/i }), { target: { value: 'Unclassified Supplier' } });
    fireEvent.click(screen.getByRole('button', { name: /save_supplier/i }));

    expect(create).not.toHaveBeenCalled();
    expect(screen.getByRole('combobox', { name: /network relationship/i })).toBeInTheDocument();
  });
});
