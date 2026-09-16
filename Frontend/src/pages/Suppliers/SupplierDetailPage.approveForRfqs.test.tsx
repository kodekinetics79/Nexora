import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import SupplierDetailPage from './SupplierDetailPage';

/**
 * A manager sent here by the sourcing case ("Open each one and have a manager approve it") met a
 * "Supplier Governance Review" dialog with FIVE selects and no statement of which combination lets
 * the supplier be asked (found driving the journey on 2026-09-15). The dialog now states the rule
 * in one sentence and offers one button that records the working combination, with an audit
 * reason; the five selects live under Advanced for the cases the preset does not cover.
 */

const getById = vi.fn();
const govern = vi.fn();
vi.mock('../../api/services/supplierService', () => ({
  default: { getById: (...a: unknown[]) => getById(...a), govern: (...a: unknown[]) => govern(...a) },
  supplierTierLabel: () => 'Not classified',
}));
vi.mock('./SupplierFormDialog', () => ({ default: () => null }));
vi.mock('../../components/common/ChangeHistoryPanel', () => ({ default: () => null }));
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { id: 1, userName: 'Rana', businessUnitId: 1, isManager: true }, hasPermission: () => true }),
}));

const fresh = {
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

async function openDialog() {
  fireEvent.click(await screen.findByRole('button', { name: /review governance/i }));
  return screen.findByRole('dialog');
}

beforeEach(() => {
  vi.clearAllMocks();
  govern.mockResolvedValue({ ...fresh, governanceStatus: 'APPROVED' });
});

describe('Supplier approval dialog — one click makes the supplier askable', () => {
  it('states the rule and records the working combination with an audit reason', async () => {
    getById.mockResolvedValue(fresh);
    renderPage();
    const dialog = await openDialog();

    expect(within(dialog).getByText(/can be asked for quotes once it is approved/i)).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: /^approve for rfqs$/i }));

    await waitFor(() => expect(govern).toHaveBeenCalledTimes(1));
    expect(govern).toHaveBeenCalledWith(1, {
      governanceStatus: 'APPROVED', verificationStatus: 'VERIFIED', complianceStatus: 'CLEARED',
      riskStatus: 'LOW', readinessStatus: 'READY',
      expectedConcurrencyToken: 'abc', reason: 'Approved for RFQs by Rana',
    });
  });

  it('keeps the five verdict selects folded under Advanced', async () => {
    getById.mockResolvedValue(fresh);
    renderPage();
    const dialog = await openDialog();

    expect(within(dialog).queryByRole('combobox', { name: /verification/i })).not.toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: /advanced/i }));
    expect(await within(dialog).findByRole('combobox', { name: /verification/i })).toBeInTheDocument();
    expect(within(dialog).getByText(/still cannot be asked: not approved, not verified, compliance not cleared, risk not assessed, not marked ready for rfqs/i)).toBeInTheDocument();
  });

  it('does not offer the preset when risk is High — that verdict is lowered by hand or not at all', async () => {
    getById.mockResolvedValue({ ...fresh, riskStatus: 'HIGH' });
    renderPage();
    const dialog = await openDialog();

    expect(within(dialog).queryByRole('button', { name: /^approve for rfqs$/i })).not.toBeInTheDocument();
    expect(within(dialog).getByText(/risk is high/i)).toBeInTheDocument();
    expect(within(dialog).getByRole('combobox', { name: /risk/i })).toBeInTheDocument();
  });
});
