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
    getAll: vi.fn().mockResolvedValue({ items: [], totalCount: 0 }),
    create: (fd: FormData) => create(fd),
    update: vi.fn(), delete: vi.fn(),
    downloadTemplate: vi.fn(), uploadTemplate: vi.fn(), export: vi.fn(),
  },
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
  create.mockResolvedValue({ id: 9, name: 'Gulf Switchgear Trading Co.' });
});

describe('SuppliersPage — saving a supplier for a sourcing case', () => {
  it('returns to the case with the refresh flag, so the new supplier is listed without a click', async () => {
    renderAt('/suppliers?new=1&tags=LV431831&returnTo=%2Fprocurement%2Fsourcing-cases%2F3');

    expect(await screen.findByText(/saving takes you back to the case/i)).toBeInTheDocument();
    fireEvent.change(screen.getByRole('textbox', { name: /supplier_name/i }), { target: { value: 'Gulf Switchgear Trading Co.' } });
    fireEvent.click(screen.getByRole('button', { name: /save_supplier/i }));

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/procurement/sourcing-cases/3?refresh=1'));
  });
});
