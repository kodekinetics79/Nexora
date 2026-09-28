import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import LeadDetailPage from './LeadDetailPage';

const getById = vi.fn();
const downloadLinesExcel = vi.fn();
const access = vi.hoisted(() => ({ entitlements: new Set<string>(['capability.exports']) }));

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => vi.fn(), useParams: () => ({ id: '77' }) };
});
vi.mock('../../api/services/leadService', () => ({
  default: {
    getById: (...a: unknown[]) => getById(...a),
    downloadLinesExcel: (...a: unknown[]) => downloadLinesExcel(...a),
  },
}));
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    hasPermission: () => true,
    hasEntitlement: (key: string) => access.entitlements.has(key),
    userData: { businessUnitId: 7, userName: 'qa', id: 1 },
  }),
}));
// Panels with data of their own; not what this spec is about.
vi.mock('./ClientIdentityPanel', () => ({ default: () => null }));
vi.mock('./ResolveClientDialog', () => ({ default: () => null }));
vi.mock('./LeadRevisionTimeline', () => ({ default: () => null }));
vi.mock('./LeadOwnerControl', () => ({ default: () => null }));
vi.mock('./LeadDecisionActions', () => ({ default: () => null }));
vi.mock('./LeadIntakeRecordDialog', () => ({ default: () => null }));
vi.mock('./LateIngestedBadge', () => ({ default: () => null }));
vi.mock('../../components/common/CommercialLineIntelligence', () => ({ default: () => null }));

const wrapper = ({ children }: { children: ReactNode }) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
};

beforeEach(() => {
  vi.clearAllMocks();
  access.entitlements = new Set(['capability.exports']);
  getById.mockResolvedValue({
    id: 77,
    rfqno: '7000999',
    customerName: 'SEC',
    businessUnitId: 7,
    reviewVersion: 1,
    leadItems: [{ id: 1, lineItemNo: '10', productShortName: 'CABLE', quantity: 500, unitOfMeasure: 'M' }],
  });
});

describe('LeadDetailPage — Download Excel', () => {
  it("downloads this lead's lines in one click", async () => {
    downloadLinesExcel.mockResolvedValue(undefined);
    render(<LeadDetailPage />, { wrapper });

    fireEvent.click(await screen.findByRole('button', { name: 'Download Excel' }));

    await waitFor(() => expect(downloadLinesExcel).toHaveBeenCalledWith(77, '7000999'));
  });

  it('is not offered when the plan has no exports', async () => {
    access.entitlements = new Set();
    render(<LeadDetailPage />, { wrapper });

    await screen.findAllByText('CABLE');
    expect(screen.queryByRole('button', { name: 'Download Excel' })).not.toBeInTheDocument();
  });
});
