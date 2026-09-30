import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * The RFQ list opens on the RFQs still to quote.
 *
 * Owner rule: an RFQ whose quote was SENT leaves the RFQ list; the work is on the Quotes list. The
 * plain list asks for readiness 'open', a typed search reaches every RFQ, and a link that names
 * 'ready-for-quote' keeps it. An empty default list must not read like "no RFQs exist" when every
 * RFQ is already quoted, and its button must lead somewhere that is not empty too.
 */

const { getAll } = vi.hoisted(() => ({ getAll: vi.fn() }));

vi.mock('../../../api/services/rfqService', () => ({ default: { getAll } }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 1 }, hasPermission: () => true }),
}));
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));
// The real button builds a workbook; here only what it would load matters.
vi.mock('../../../components/common/ExportExcelButton', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../components/common/ExportExcelButton')>();
  return {
    ...actual,
    default: ({ loadRows }: { loadRows: () => Promise<unknown> }) => (
      <button type="button" onClick={() => { void loadRows(); }}>Export to Excel</button>
    ),
  };
});

import AllRFQsPage from './AllRFQsPage';

type Params = { pageSize?: number; pageNumber?: number; search?: string; readiness?: string };
const calls = (): Params[] => getAll.mock.calls.map((call) => call[0] as Params);
/** The readiness the GRID last asked for (pageSize 25); pageSize 1 is the unfiltered total. */
const lastGridReadiness = () => calls().filter((p) => p.pageSize === 25).at(-1)?.readiness;

const renderPage = (url = '/procurement/rfqs/all') => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const Address = () => <div data-testid="address">{useLocation().pathname}</div>;
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[url]}>
        <Routes>
          <Route path="*" element={<><AllRFQsPage /><Address /></>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
};

const typeSearch = (value: string) =>
  fireEvent.change(screen.getByPlaceholderText(/search rfq/i), { target: { value } });

beforeEach(() => {
  vi.clearAllMocks();
  // Three RFQs exist (the pageSize-1 unfiltered read); none is left to quote.
  getAll.mockImplementation((params: Params) => Promise.resolve(
    params.pageSize === 1
      ? { items: [], totalItems: 3, pageNumber: 1, pageSize: 1 }
      : { items: [], totalItems: 0, pageNumber: params.pageNumber ?? 1, pageSize: params.pageSize ?? 25 },
  ));
});

describe('AllRFQsPage — opens on the RFQs still to quote', () => {
  it("asks for readiness 'open' by default", async () => {
    renderPage();
    await waitFor(() => expect(lastGridReadiness()).toBe('open'));
  });

  it('reaches every RFQ while a search is typed, and returns to open when it is cleared', async () => {
    renderPage();
    await waitFor(() => expect(lastGridReadiness()).toBe('open'));

    typeSearch('RFQ-1');
    await waitFor(() => expect(calls().some((p) => p.pageSize === 25 && p.search === 'RFQ-1')).toBe(true));
    expect(calls().filter((p) => p.pageSize === 25 && p.search === 'RFQ-1').every((p) => p.readiness === undefined)).toBe(true);

    typeSearch('');
    await waitFor(() => expect(lastGridReadiness()).toBe('open'));
    expect(calls().filter((p) => p.pageSize === 25).at(-1)?.search).toBeUndefined();
  });

  it("keeps ?state=ready-for-quote with and without a search", async () => {
    renderPage('/procurement/rfqs/all?state=ready-for-quote');
    await waitFor(() => expect(lastGridReadiness()).toBe('ready-for-quote'));

    typeSearch('RFQ-1');
    await waitFor(() => expect(calls().some((p) => p.pageSize === 25 && p.search === 'RFQ-1')).toBe(true));
    expect(lastGridReadiness()).toBe('ready-for-quote');
  });

  it('exports the same RFQs the grid shows', async () => {
    renderPage();
    await waitFor(() => expect(lastGridReadiness()).toBe('open'));
    const exportCalls = () => calls().filter((p) => p.pageSize !== 25 && p.pageSize !== 1);

    fireEvent.click(screen.getByRole('button', { name: 'Export to Excel' }));
    await waitFor(() => expect(exportCalls().length).toBeGreaterThan(0));
    expect(exportCalls().at(-1)?.readiness).toBe('open');

    typeSearch('RFQ-1');
    await waitFor(() => expect(lastGridReadiness()).toBeUndefined());
    fireEvent.click(screen.getByRole('button', { name: 'Export to Excel' }));
    await waitFor(() => expect(exportCalls().some((p) => p.search === 'RFQ-1')).toBe(true));
    expect(exportCalls().at(-1)?.readiness).toBeUndefined();
  });

  it('says "Nothing to quote" and opens the quotes when every RFQ is already quoted', async () => {
    renderPage();
    expect(await screen.findByText('Nothing to quote')).toBeInTheDocument();
    expect(screen.queryByText('No RFQs yet')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Open quotes' }));
    expect(screen.getByTestId('address')).toHaveTextContent('/sales/quotes');
  });

  it('keeps "No RFQs yet" when there are no RFQs at all', async () => {
    getAll.mockImplementation((params: Params) => Promise.resolve(
      { items: [], totalItems: 0, pageNumber: 1, pageSize: params.pageSize ?? 25 },
    ));
    renderPage();
    expect(await screen.findByText('No RFQs yet')).toBeInTheDocument();
    expect(screen.queryByText('Nothing to quote')).not.toBeInTheDocument();
  });

  it('offers "Clear search" when only a search emptied the list', async () => {
    renderPage();
    await waitFor(() => expect(lastGridReadiness()).toBe('open'));
    typeSearch('nothing-like-this');
    fireEvent.click(await screen.findByRole('button', { name: 'Clear search' }));
    expect(screen.getByPlaceholderText(/search rfq/i)).toHaveValue('');
    await waitFor(() => expect(lastGridReadiness()).toBe('open'));
  });
});
