import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import SupplierDetailPage from './SupplierDetailPage';

/**
 * The page has three early returns (loading, error, not found) and a governance mutation. The
 * mutation was declared AFTER those returns, so the first render (loading) registered one hook
 * fewer than the second (loaded): React #310, and the whole screen "stopped working" the moment
 * the supplier arrived — on every open, for every user (found driving the sourcing journey on
 * 2026-09-15). This test walks that exact transition: a deferred fetch, then the loaded screen.
 */

const getById = vi.fn();
vi.mock('../../api/services/supplierService', () => ({
  default: { getById: (...a: unknown[]) => getById(...a), govern: vi.fn() },
  supplierTierLabel: () => 'Not classified',
}));
vi.mock('./SupplierFormDialog', () => ({ default: () => null }));
vi.mock('../../components/common/ChangeHistoryPanel', () => ({ default: () => null }));
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { id: 1, businessUnitId: 1, isManager: true }, hasPermission: () => true }),
}));

const supplier = {
  id: 1, name: 'Gulf Switchgear Trading Co.', contactEmail: 'sales@gulfswitchgear.example',
  governanceStatus: 'UNVERIFIED', verificationStatus: 'UNKNOWN', complianceStatus: 'UNKNOWN',
  riskStatus: 'UNKNOWN', readinessStatus: 'REVIEW_REQUIRED', isActive: true, tags: 'LV431831',
  concurrencyToken: 'abc',
};

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/suppliers/1']}>
        <Routes><Route path="/suppliers/:id" element={<SupplierDetailPage />} /></Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('Supplier detail — survives the loading → loaded transition', () => {
  it('renders the supplier after a deferred fetch instead of throwing (React #310)', async () => {
    let resolve!: (value: typeof supplier) => void;
    getById.mockReturnValue(new Promise<typeof supplier>((r) => { resolve = r; }));
    const errors = vi.spyOn(console, 'error').mockImplementation(() => {});

    renderPage();
    expect(screen.getByRole('progressbar')).toBeInTheDocument();

    resolve(supplier);
    expect((await screen.findAllByText('Gulf Switchgear Trading Co.')).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: /review governance/i })).toBeInTheDocument();
    expect(errors.mock.calls.some((c) => String(c[0]).includes('310') || String(c[0]).includes('more hooks'))).toBe(false);
    errors.mockRestore();
  });
});
