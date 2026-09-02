import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * "Refresh candidates" is gated twice — on Supplier History: Edit and on the case not having
 * moved past candidate selection — and used to go grey with no reason. A rep who could review
 * candidates but not refresh them had no way to tell a missing permission from a passed step.
 * The prepare button beside it already prints its reason ("Your role can review candidates but
 * cannot prepare Supplier RFQs."); this pins the same treatment for refresh.
 */

const auth = { grants: null as Set<string> | null };
const getSourcingCase = vi.fn();

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useParams: () => ({ caseId: '3' }), useNavigate: () => vi.fn() };
});
vi.mock('../../../api/services/procurementService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/procurementService')>();
  return { ...actual, default: { ...actual.default, getSourcingCase: () => getSourcingCase() } };
});
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: 7 },
    hasPermission: (module: string, action?: string) =>
      auth.grants === null || auth.grants.has(`${module}:${action ?? 'view'}`),
  }),
}));

const SourcingCasePage = (await import('./SourcingCasePage')).default;

const sourcingCase = (status: string) => ({
  id: 3, commercialDemandLineId: 1, rfqId: 5, rfqItemId: 10, nexoraSerial: 'NX-1',
  description: 'Pressure transmitter', requestedQuantity: 5, stockQuantity: 0, unfulfilledQuantity: 5,
  searchLimit: 10, status, nextAction: 'SELECT_CANDIDATES', version: 1, candidates: [],
});

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter><SourcingCasePage /></MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  auth.grants = null;
});

describe('SourcingCasePage — Refresh candidates says why it is disabled', () => {
  it('names the missing permission', async () => {
    auth.grants = new Set(['RFQ Management:edit', 'Supplier History:create', 'Supplier History:view']);
    getSourcingCase.mockResolvedValue(sourcingCase('CANDIDATES_READY'));
    renderPage();

    const button = await screen.findByRole('button', { name: /refresh candidates/i });
    expect(button).toBeDisabled();
    fireEvent.mouseOver(button.parentElement as HTMLElement);
    expect(await screen.findByText(/your role can review candidates but cannot refresh them/i)).toBeInTheDocument();
  });

  it('says the step has passed once a Supplier RFQ has been prepared', async () => {
    getSourcingCase.mockResolvedValue(sourcingCase('OUTREACH_SENT'));
    renderPage();

    const button = await screen.findByRole('button', { name: /refresh candidates/i });
    expect(button).toBeDisabled();
    fireEvent.mouseOver(button.parentElement as HTMLElement);
    expect(await screen.findByText(/candidates are fixed once a supplier rfq has been prepared/i)).toBeInTheDocument();
  });

  it('prints nothing when the button works (the control for the two above)', async () => {
    getSourcingCase.mockResolvedValue(sourcingCase('CANDIDATES_READY'));
    renderPage();

    const button = await screen.findByRole('button', { name: /refresh candidates/i });
    expect(button).toBeEnabled();
    fireEvent.mouseOver(button.parentElement as HTMLElement);
    expect(screen.queryByText(/cannot refresh them|candidates are fixed/i)).not.toBeInTheDocument();
  });
});
