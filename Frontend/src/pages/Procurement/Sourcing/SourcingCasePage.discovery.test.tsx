import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * When no supplier on the tenant's list is linked to the part, the case used to stop at "No internet
 * search is run" and an "Add a supplier" button (owner decision 2026-09-16). The page now searches the
 * internet by itself, lists what it found manufacturer → distributor → reseller, ten at a time, and the
 * rep ticks who to add; they become tenant suppliers, appear in the known list above, and the existing
 * tick → "Ask N suppliers" flow sends the RFQs. Known suppliers always stay first.
 */

const getSourcingCase = vi.fn();
const discoverSuppliers = vi.fn();
const adoptDiscoveredSuppliers = vi.fn();
const toastSuccess = vi.fn();
const navigate = vi.fn();

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useParams: () => ({ caseId: '3' }), useNavigate: () => navigate };
});
vi.mock('../../../api/services/procurementService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/procurementService')>();
  return {
    ...actual,
    default: {
      ...actual.default,
      getSourcingCase: () => getSourcingCase(),
      discoverSuppliers: (...args: unknown[]) => discoverSuppliers(...args),
      adoptDiscoveredSuppliers: (...args: unknown[]) => adoptDiscoveredSuppliers(...args),
    },
  };
});
vi.mock('react-hot-toast', () => ({
  toast: { success: (...args: unknown[]) => toastSuccess(...args), error: vi.fn() },
}));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7, userName: 'Rana' }, hasPermission: () => true }),
}));

const SourcingCasePage = (await import('./SourcingCasePage')).default;

const PART = 'LV431831';

const sourcingCase = (candidates: unknown[] = []) => ({
  id: 3, commercialDemandLineId: 1, rfqId: 5, rfqItemId: 10, nexoraSerial: 'NX-1', requestedPartNumber: PART,
  description: 'MCCB 250A 3P', requestedQuantity: 1, stockQuantity: 0, unfulfilledQuantity: 1,
  searchLimit: 10, status: 'DISCOVERY_REQUIRED', nextAction: 'Review discovery options', version: 1, candidates,
});

const hit = (n: number, role: 'Manufacturer' | 'Distributor' | 'Reseller', over: Record<string, unknown> = {}) => ({
  id: `hit-${n}`, name: `Supplier ${n}`, website: `https://supplier${n}.example`, domain: `supplier${n}.example`,
  role, country: n % 2 ? 'Saudi Arabia' : null, why: `Lists ${PART} on its site (${n}).`, contactEmail: n % 3 ? `sales@supplier${n}.example` : null,
  existingSupplierId: null, ...over,
});

// 12 hits, already ranked by the server: 4 makers, 4 distributors, 4 resellers, in two pages of 10 + 2.
const ALL_HITS = [
  ...[1, 2, 3, 4].map((n) => hit(n, 'Manufacturer')),
  ...[5, 6, 7, 8].map((n) => hit(n, 'Distributor')),
  ...[9, 10, 11, 12].map((n) => hit(n, 'Reseller')),
];

const ready = (offset: number, limit = 10) => ({
  status: 'Ready', message: '', total: ALL_HITS.length, offset, limit, fromCache: false, searchedAtUtc: '2026-09-16T08:00:00Z',
  searchedFor: { maker: 'Schneider Electric', partNumber: PART, description: 'MCCB 250A 3P', acceptableMakers: ['ABB', 'Siemens'] },
  hits: ALL_HITS.slice(offset, offset + limit),
});

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter initialEntries={['/procurement/sourcing-cases/3']}><SourcingCasePage /></MemoryRouter>
  </QueryClientProvider>,
);

const internetList = async () => screen.findByTestId('internet-suppliers');

beforeEach(() => {
  vi.clearAllMocks();
  getSourcingCase.mockResolvedValue(sourcingCase());
  discoverSuppliers.mockImplementation((_caseId: number, page: { offset: number }) => Promise.resolve(ready(page.offset)));
});

describe('SourcingCasePage — suppliers from the internet', () => {
  it('searches by itself when fewer than ten known suppliers match, lists ten in the server\'s order with a role chip each, and "Show more" fetches the next page', async () => {
    renderPage();

    const list = await internetList();
    expect(discoverSuppliers).toHaveBeenCalledWith(3, { offset: 0, limit: 10 });
    const rows = await within(list).findAllByRole('row');
    const bodyRows = rows.filter((row) => within(row).queryByRole('link'));
    expect(bodyRows).toHaveLength(10);
    expect(bodyRows.map((row) => within(row).getByRole('link').textContent)).toEqual(
      ALL_HITS.slice(0, 10).map((h) => h.name),
    );
    expect(bodyRows.map((row) => within(row).getByTestId('discovery-role').textContent)).toEqual([
      'Manufacturer', 'Manufacturer', 'Manufacturer', 'Manufacturer',
      'Distributor', 'Distributor', 'Distributor', 'Distributor',
      'Reseller', 'Reseller',
    ]);
    const link = within(bodyRows[0]).getByRole('link', { name: 'Supplier 1' });
    expect(link).toHaveAttribute('href', 'https://supplier1.example');
    expect(link).toHaveAttribute('target', '_blank');
    expect(link.getAttribute('rel')).toContain('noopener');
    expect(within(bodyRows[0]).getByText(`Lists ${PART} on its site (1).`)).toBeInTheDocument();
    expect(screen.getByText('Searched for: Schneider Electric LV431831 · also acceptable: ABB, Siemens')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Show 2 more' }));
    await waitFor(() => expect(discoverSuppliers).toHaveBeenCalledWith(3, { offset: 10, limit: 10 }));
    await waitFor(() => expect(within(list).getAllByRole('checkbox')).toHaveLength(12));
    expect(screen.queryByRole('button', { name: /Show \d+ more/ })).not.toBeInTheDocument();
  });

  it('shows the server\'s sentence when the search cannot run and keeps "Add a supplier" as the one filled button', async () => {
    discoverSuppliers.mockResolvedValue({
      ...ready(0), status: 'NotConfigured', total: 0, hits: [],
      message: 'Internet search is not switched on for your company yet. Ask your administrator.',
    });
    renderPage();

    const panel = await screen.findByTestId('sourcing-case-next-step');
    await waitFor(() => expect(within(panel).getByText('Internet search is not switched on for your company yet. Ask your administrator.')).toBeInTheDocument());
    expect(within(panel).getByText(/No supplier on your list is linked to LV431831 yet/)).toBeInTheDocument();
    const add = screen.getByRole('button', { name: 'Add a supplier' });
    expect(add.className).toContain('MuiButton-contained');
    expect(screen.queryByRole('button', { name: /to my suppliers/ })).not.toBeInTheDocument();
    expect(screen.queryByText(/provider|cache|egress/i)).not.toBeInTheDocument();
  });

  it('counts the ticks on the button, adds the ticked suppliers, refetches the case and says where they went', async () => {
    adoptDiscoveredSuppliers.mockResolvedValue({
      adopted: [
        { hitId: 'hit-1', supplierId: 101, supplierName: 'Supplier 1', contactEmail: 'sales@supplier1.example', needsContactEmail: false, alreadyExisted: false },
        { hitId: 'hit-3', supplierId: 103, supplierName: 'Supplier 3', contactEmail: null, needsContactEmail: true, alreadyExisted: false },
      ],
    });
    renderPage();
    await internetList();

    const addButton = await screen.findByRole('button', { name: 'Add the ticked suppliers' });
    expect(addButton).toBeDisabled();
    expect(addButton.className).toContain('MuiButton-outlined');

    fireEvent.click(screen.getByRole('checkbox', { name: 'Add Supplier 1' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Add Supplier 3' }));
    const add = screen.getByRole('button', { name: 'Add 2 to my suppliers' });
    expect(add).toBeEnabled();
    expect(add.className).toContain('MuiButton-contained');
    expect(screen.getByText('2 ticked')).toBeInTheDocument();

    fireEvent.click(add);
    await waitFor(() => expect(adoptDiscoveredSuppliers).toHaveBeenCalledWith(3, ['hit-1', 'hit-3']));
    await waitFor(() => expect(getSourcingCase).toHaveBeenCalledTimes(2));
    expect(toastSuccess).toHaveBeenCalledWith('Added 2 suppliers. They are now in your list above; approve them for RFQs to ask them.');
    expect(screen.getAllByText('Added')).toHaveLength(2);
    const supplierPage = screen.getByRole('button', { name: 'supplier page' });
    expect(supplierPage.parentElement).toHaveTextContent('No email found — add one on the supplier page before asking.');
    fireEvent.click(supplierPage);
    expect(navigate).toHaveBeenCalledWith('/suppliers/103');
    expect(screen.queryByRole('checkbox', { name: 'Add Supplier 1' })).not.toBeInTheDocument();
  });

  it('offers no tick box for a hit that is already one of the tenant\'s suppliers', async () => {
    discoverSuppliers.mockResolvedValue({
      ...ready(0), total: 2, hits: [hit(1, 'Manufacturer', { existingSupplierId: 44 }), hit(2, 'Distributor')],
    });
    renderPage();

    const list = await internetList();
    await within(list).findByText('Already on your list');
    expect(within(list).queryByRole('checkbox', { name: 'Add Supplier 1' })).not.toBeInTheDocument();
    expect(within(list).getByRole('checkbox', { name: 'Add Supplier 2' })).toBeInTheDocument();
  });

  it('tells the rep how many the internet turned up when nobody on their list is linked to the part, and moves the filled button to the ticks', async () => {
    renderPage();

    const panel = await screen.findByTestId('sourcing-case-next-step');
    await waitFor(() => expect(within(panel).getByText(
      'No supplier on your list is linked to LV431831 yet. We found 12 on the internet — tick the ones to add, or add one yourself.',
    )).toBeInTheDocument());
    expect(within(panel).getByRole('button', { name: 'Add a supplier' }).className).toContain('MuiButton-contained');

    fireEvent.click(screen.getByRole('checkbox', { name: 'Add Supplier 2' }));
    expect(within(panel).getByText('1 ticked. Press Add 1 to my suppliers; it joins your list above.')).toBeInTheDocument();
    expect(within(panel).getByRole('button', { name: 'Add a supplier' }).className).toContain('MuiButton-outlined');
    const contained = screen.getAllByRole('button').filter((b) => b.className.includes('MuiButton-contained'));
    expect(contained.map((b) => b.textContent)).toEqual(['Add 1 to my suppliers']);
  });

  it('shows a progress line while searching and never blocks the known list', async () => {
    let resolve: (value: unknown) => void = () => {};
    discoverSuppliers.mockReturnValue(new Promise((r) => { resolve = r; }));
    renderPage();

    expect(await screen.findByText(`Searching the internet for ${PART}…`)).toBeInTheDocument();
    expect(screen.getByText('No supplier is linked to this part yet')).toBeInTheDocument();
    resolve(ready(0));
    await within(await internetList()).findByRole('checkbox', { name: 'Add Supplier 1' });
    expect(screen.queryByText(`Searching the internet for ${PART}…`)).not.toBeInTheDocument();
  });

  it('does not search by itself when ten known suppliers already match, but offers the search on demand', async () => {
    const known = Array.from({ length: 10 }, (_, i) => ({
      id: i + 1, supplierId: 20 + i, supplierName: `Known ${i + 1}`, contactEmail: `k${i}@example.com`, rank: i + 1,
      evidenceType: 'SUPPLIER_METADATA', recommendationReason: 'Maker named in Tags', evidenceScore: 50, selected: false,
      governanceStatus: 'APPROVED', readinessStatus: 'READY', eligibleForSupplierRfq: true, blockingReasons: [],
    }));
    getSourcingCase.mockResolvedValue({ ...sourcingCase(known), status: 'CANDIDATES_READY' });
    renderPage();

    await screen.findByRole('checkbox', { name: 'Select Known 1' });
    expect(discoverSuppliers).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Search the internet' }));
    await waitFor(() => expect(discoverSuppliers).toHaveBeenCalledWith(3, { offset: 0, limit: 10 }));
    await within(await internetList()).findByRole('checkbox', { name: 'Add Supplier 1' });
    // Known suppliers stay first: the internet list sits below the known table.
    const knownBox = screen.getByRole('checkbox', { name: 'Select Known 1' });
    const internetBox = screen.getByRole('checkbox', { name: 'Add Supplier 1' });
    expect(knownBox.compareDocumentPosition(internetBox) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
});
