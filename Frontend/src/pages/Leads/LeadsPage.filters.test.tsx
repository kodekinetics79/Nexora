import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import LeadsPage, { composeLeadsView } from './LeadsPage';

// This file drives the full DataGrid page, often in the wide spreadsheet view, through jsdom; 5s is
// not enough under a parallel run on a cold machine (the sibling LeadsPage files set the same).
vi.setConfig({ testTimeout: 30_000 });

/**
 * The Leads filters, all in the column headers (owner 2026-09-29: "give filter on all fields",
 * "rethink and rearrange" the filter row away).
 *
 * What matters to the reader is that the list on screen, the list in Export to Excel, and the URL
 * they can send to a colleague all say the same thing — and that the row of filter controls above
 * the grid is gone, with the view controls on the tabs line.
 */

const getAll = vi.fn();
const getListCustomers = vi.fn();
const getOwnerOptions = vi.fn();
const auth = vi.hoisted(() => ({ userData: {} as Record<string, unknown> }));

vi.mock('../../api/services/leadService', () => ({
  default: {
    getAll: (params: unknown) => getAll(params),
    getListCustomers: (view: unknown, due: unknown) => getListCustomers(view, due),
    fetchEmails: vi.fn(),
  },
}));
vi.mock('../../api/services/decisionService', () => ({
  default: { getDecisionSummaries: vi.fn().mockResolvedValue({ summaries: {} }) },
}));
vi.mock('../../api/services/commercialRoutingService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/commercialRoutingService')>();
  return { ...actual, default: { getOwnerOptions: () => getOwnerOptions(), changeLeadOwner: vi.fn(), getLeadAssignmentHistory: vi.fn().mockResolvedValue([]) } };
});
vi.mock('../../hooks/useColumnPreferences', () => ({
  default: () => ({ columnVisibilityModel: {}, onColumnVisibilityModelChange: vi.fn(), arrangeColumns: <T,>(defs: T) => defs, isLoading: false, isError: false }),
}));
vi.mock('../../components/common/ColumnPreferences', () => ({ default: () => null }));
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true, userData: auth.userData }),
}));
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));
vi.mock('react-hot-toast', () => ({ toast: Object.assign(vi.fn(), { error: vi.fn() }) }));
vi.mock('xlsx', async (importOriginal) => {
  const actual = await importOriginal<typeof import('xlsx')>();
  return { ...actual, writeFile: vi.fn() };
});

type Params = {
  pageSize?: number; pageNumber?: number; view?: string; customer?: string; due?: string; today?: string; search?: string;
  rfqno?: string; serial?: string; rfq?: string; buyer?: string; agreement?: string; dueFrom?: string; dueTo?: string;
  startDate?: string; endDate?: string; ingestedFrom?: string; ingestedBefore?: string; requiredFrom?: string; requiredTo?: string; itemsMin?: number; itemsMax?: number;
  status?: string; leadSource?: string;
};
const calls = (): Params[] => getAll.mock.calls.map((call) => call[0] as Params);
const gridCalls = (): Params[] => calls().filter((p) => p.pageSize !== 1 && p.pageSize !== 500);
const lastGrid = (): Params | undefined => gridCalls().at(-1);

const localDay = (offset = 0) => {
  const now = new Date();
  const day = new Date(now.getFullYear(), now.getMonth(), now.getDate() + offset);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${day.getFullYear()}-${pad(day.getMonth() + 1)}-${pad(day.getDate())}`;
};
const localToday = () => localDay(0);

/** The address bar, as the page last wrote it. */
const url = { search: '' };
const LocationSpy = () => {
  url.search = useLocation().search;
  return null;
};
const urlParams = () => new URLSearchParams(url.search);

const renderPage = (route = '/procurement/leads/all') => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <MemoryRouter initialEntries={[route]}>
      <LocationSpy />
      <SnackbarProvider>
        <QueryClientProvider client={client}>
          <LeadsPage />
        </QueryClientProvider>
      </SnackbarProvider>
    </MemoryRouter>,
  );
};

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&');

/** Open a column header's filter and choose one entry (from the named list when it has several). */
const pick = (noun: string, entry: string, list?: string) => {
  fireEvent.click(screen.getByRole('button', { name: `Filter by ${noun}` }));
  const menu = list ? screen.getByRole('menu', { name: list }) : screen.getByRole('menu');
  fireEvent.click(within(menu).getByRole('menuitem', { name: new RegExp(`^${escape(entry)}`) }));
};

/** Type into a column's "Contains…" filter and apply it. */
const capital = (noun: string) => noun.charAt(0).toUpperCase() + noun.slice(1);
const typeFilter = (noun: string, text: string) => {
  fireEvent.click(screen.getByRole('button', { name: `Filter by ${noun}` }));
  fireEvent.change(screen.getByRole('textbox', { name: `${capital(noun)} contains` }), { target: { value: text } });
  fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
};

/** The spreadsheet view carries every column; it is the reader's saved choice. */
const asSpreadsheet = () => localStorage.setItem('nexora.leadsPage.view:global', 'spreadsheet');

const rows = Array.from({ length: 25 }, (_, i) => ({ id: i + 1, rfqno: `RFQ-${i + 1}` }));

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  url.search = '';
  auth.userData = { id: 7 };
  getAll.mockImplementation((params: Params) => Promise.resolve(
    params.pageSize === 1
      ? { items: [], totalCount: 60, pageNumber: 1, pageSize: 1 }
      : { items: params.pageSize === 500 ? rows.slice(0, 2) : rows, totalCount: 60, pageNumber: params.pageNumber ?? 1, pageSize: params.pageSize ?? 25 },
  ));
  getListCustomers.mockResolvedValue({
    customers: [
      { customerId: 30, name: 'Saudi Electricity Company', count: 12 },
      { customerId: 31, name: 'Marafiq', count: 4 },
    ],
    noCustomer: 3,
    statuses: [
      { statusId: 5, code: 'QUALIFIED', label: 'Qualified (tenant words)', count: 2 },
      { statusId: 9, code: 'SOMETHING_NEW', label: 'On hold', count: 1 },
    ],
    notOpened: 8,
  });
  getOwnerOptions.mockResolvedValue([
    { userId: 42, name: 'Omar Rep', email: 'omar@x', isAvailable: true, acceptsManualAssignment: true, capacityPercent: 10, eligibilityReason: '' },
    { userId: 7, name: 'Me Manager', email: 'me@x', isAvailable: true, acceptsManualAssignment: true, capacityPercent: 0, eligibilityReason: '' },
  ]);
});

describe('composeLeadsView', () => {
  it('adds one rep only alongside Everyone', () => {
    expect(composeLeadsView('queue', 'all', 7, 42)).toBe('queue,rep:42');
    expect(composeLeadsView('queue', 'mine', 7, 42)).toBe('queue,mine:7');
    expect(composeLeadsView('queue', 'unassigned', 7, 42)).toBe('queue,unassigned');
    expect(composeLeadsView('queue', 'all', 7)).toBe('queue');
    expect(composeLeadsView(undefined, 'all', 7, null)).toBeUndefined();
  });
});

describe('LeadsPage — paging', () => {
  it('stays on page 2 after "next page" instead of bouncing back while it loads', async () => {
    // A real server answers later than the grid re-renders; the page must not reset meanwhile.
    getAll.mockImplementation((params: Params) => new Promise((resolve) => setTimeout(() => resolve(
      params.pageSize === 1
        ? { items: [], totalCount: 60, pageNumber: 1, pageSize: 1 }
        : { items: rows.map((r) => ({ ...r, id: r.id + ((params.pageNumber ?? 1) - 1) * 25 })), totalCount: 60, pageNumber: params.pageNumber ?? 1, pageSize: 25 },
    ), 10)));
    renderPage();
    const next = await screen.findByRole('button', { name: /go to next page/i });
    await waitFor(() => expect(next).toBeEnabled());
    fireEvent.click(next);
    expect(await screen.findByText('26–50 of 60')).toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(lastGrid()?.pageNumber).toBe(2);
    expect(screen.getByText('26–50 of 60')).toBeInTheDocument();
  });
});

describe('LeadsPage — header filters', () => {
  it('reads customer and due from the URL into the grid call, and leaves the total count unfiltered', async () => {
    renderPage('/procurement/leads/all?customer=30&due=overdue');
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ customer: '30', due: 'overdue', today: localToday() })));
    const totals = calls().filter((p) => p.pageSize === 1);
    expect(totals.length).toBeGreaterThan(0);
    totals.forEach((p) => {
      expect(p.customer).toBeUndefined();
      expect(p.due).toBeUndefined();
      expect(p.today).toBeUndefined();
    });
  });

  it('sends nothing for Any: no due and no today', async () => {
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    expect(lastGrid()).not.toHaveProperty('due', expect.anything());
    expect(lastGrid()?.today).toBeUndefined();
    expect(screen.getByRole('button', { name: 'Filter by bid due date' })).toHaveAttribute('aria-pressed', 'false');
  });

  it('"Next 7 days" in the Deadline header sends due 7d with today, from page 1', async () => {
    renderPage();
    const next = await screen.findByRole('button', { name: /go to next page/i });
    await waitFor(() => expect(next).toBeEnabled());
    fireEvent.click(next);
    await waitFor(() => expect(lastGrid()?.pageNumber).toBe(2));

    pick('bid due date', 'Next 7 days');
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ due: '7d', today: localToday(), pageNumber: 1 })));
    // Never page 2 of the narrower list, not even for one request.
    expect(gridCalls().filter((p) => p.due === '7d').every((p) => p.pageNumber === 1)).toBe(true);
    expect(screen.getByRole('button', { name: 'Filter by bid due date' })).toHaveAttribute('aria-pressed', 'true');
  });

  it("the Customer header offers this list's customers with counts, finds as you type, and sends the one picked", async () => {
    renderPage();
    await waitFor(() => expect(getListCustomers).toHaveBeenCalledWith('queue', {}));
    fireEvent.click(screen.getByRole('button', { name: 'Filter by customer' }));
    fireEvent.change(await screen.findByRole('textbox', { name: 'Find a customer' }), { target: { value: 'SAUDI' } });
    const menu = await screen.findByRole('menu', { name: 'Customer' });
    expect(within(menu).getAllByRole('menuitem').map((o) => o.textContent)).toEqual(['Any customer', 'Saudi Electricity Company (12)']);

    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Saudi Electricity Company (12)' }));
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ customer: '30', pageNumber: 1 })));
  });

  it('counts each customer under the date filter that is on', async () => {
    renderPage('/procurement/leads/all?due=overdue');
    await waitFor(() => expect(getListCustomers).toHaveBeenCalledWith('queue', { due: 'overdue', today: localToday() }));
  });

  it('offers "No customer yet" last and sends customer=none', async () => {
    renderPage();
    await waitFor(() => expect(getListCustomers).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('button', { name: 'Filter by customer' }));
    const menu = await screen.findByRole('menu', { name: 'Customer' });
    await waitFor(() => expect(within(menu).getAllByRole('menuitem').at(-1)).toHaveTextContent('No customer yet (3)'));
    fireEvent.click(within(menu).getAllByRole('menuitem').at(-1)!);
    await waitFor(() => expect(lastGrid()?.customer).toBe('none'));
  });

  it('Export to Excel asks for exactly the list on screen', async () => {
    renderPage('/procurement/leads/all?customer=30&due=14d');
    await waitFor(() => expect(lastGrid()?.customer).toBe('30'));
    const grid = lastGrid()!;

    fireEvent.click(screen.getByRole('button', { name: /export to excel/i }));
    await waitFor(() => expect(calls().some((p) => p.pageSize === 500)).toBe(true));
    const exported = calls().find((p) => p.pageSize === 500)!;
    const strip = ({ pageNumber: _n, pageSize: _s, ...rest }: Params) => rest;
    expect(strip(exported)).toEqual(strip(grid));
    expect(exported).toEqual(expect.objectContaining({ customer: '30', due: '14d', today: localToday() }));
  });

  it('the Owner header is Anyone · Unassigned · Mine, then each rep for a manager — mapped to the view tokens', async () => {
    auth.userData = { id: 7, isManager: true };
    renderPage();
    await waitFor(() => expect(getOwnerOptions).toHaveBeenCalled());
    await waitFor(() => expect(lastGrid()?.view).toBe('queue'));

    fireEvent.click(screen.getByRole('button', { name: 'Filter by owner' }));
    const menu = screen.getByRole('menu');
    await waitFor(() => expect(within(menu).getAllByRole('menuitem').map((o) => o.textContent))
      .toEqual(['Anyone', 'Unassigned', 'Mine', 'Omar Rep', 'Me Manager']));
    expect(within(menu).getByRole('menuitem', { name: 'Anyone' })).toHaveClass('Mui-selected');
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Mine' }));
    await waitFor(() => expect(lastGrid()?.view).toBe('queue,mine:7'));
    expect(urlParams().get('owner')).toBe('mine');

    pick('owner', 'Omar Rep');
    await waitFor(() => expect(lastGrid()?.view).toBe('queue,rep:42'));
    expect(screen.getByRole('button', { name: 'Filter by owner' })).toHaveAttribute('aria-pressed', 'true');
    // One rep's list is a slice of everyone's: Mine went, the rep is on the URL.
    expect(urlParams().has('owner')).toBe(false);
    expect(urlParams().get('rep')).toBe('42');

    // Unassigned answers a different question: the rep goes.
    pick('owner', 'Unassigned');
    await waitFor(() => expect(lastGrid()?.view).toBe('queue,unassigned'));
    expect(urlParams().has('rep')).toBe(false);

    pick('owner', 'Anyone');
    await waitFor(() => expect(lastGrid()?.view).toBe('queue'));
    expect(screen.getByRole('button', { name: 'Filter by owner' })).toHaveAttribute('aria-pressed', 'false');
  });

  it('does not offer reps to a non-manager, and ignores rep= on their URL', async () => {
    renderPage('/procurement/leads/all?rep=42');
    await waitFor(() => expect(lastGrid()).toBeDefined());
    expect(lastGrid()?.view).toBe('queue');
    await waitFor(() => expect(getOwnerOptions).toHaveBeenCalled());
    expect(screen.getByRole('button', { name: 'Filter by owner' })).toHaveAttribute('aria-pressed', 'false');
    fireEvent.click(screen.getByRole('button', { name: 'Filter by owner' }));
    expect(within(screen.getByRole('menu')).getAllByRole('menuitem').map((o) => o.textContent)).toEqual(['Anyone', 'Unassigned', 'Mine']);
  });

  it('keeps the owner and where-it-came-from on the URL, so a link or Back brings them back', async () => {
    renderPage('/procurement/leads/all?owner=unassigned&source=Email');
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ view: 'queue,unassigned', leadSource: 'Email' })));
  });

  it('says "No inquiries match" with the filters in words, and Clear filters drops them', async () => {
    getAll.mockImplementation((params: Params) => Promise.resolve(
      params.pageSize === 1
        ? { items: [], totalCount: 3, pageNumber: 1, pageSize: 1 }
        : { items: [], totalCount: 0, pageNumber: 1, pageSize: 25 },
    ));
    renderPage('/procurement/leads/all?customer=30&due=overdue');
    expect(await screen.findByText('No inquiries match')).toBeInTheDocument();
    expect(await screen.findByText('Saudi Electricity Company · Overdue')).toBeInTheDocument();

    const clearButtons = screen.getAllByRole('button', { name: /clear filters/i });
    fireEvent.click(clearButtons.at(-1)!);
    await waitFor(() => {
      expect(lastGrid()?.customer).toBeUndefined();
      expect(lastGrid()?.due).toBeUndefined();
    });
    expect(await screen.findByText('Nothing to decide')).toBeInTheDocument();
  });

  it('choosing "Any date" in the header drops the date filter', async () => {
    renderPage('/procurement/leads/all?due=7d');
    await waitFor(() => expect(lastGrid()?.due).toBe('7d'));
    pick('bid due date', 'Any date');
    await waitFor(() => expect(lastGrid()?.due).toBeUndefined());
    expect(screen.getByRole('button', { name: 'Filter by bid due date' })).toHaveAttribute('aria-pressed', 'false');
  });

  it('has no filter row: Simple/Spreadsheet, Display and Clear filters sit on the tabs line', async () => {
    renderPage('/procurement/leads/all?view=revisions');
    await waitFor(() => expect(lastGrid()).toBeDefined());
    expect(screen.queryByLabelText(/where it came from/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Owner' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /not opened yet/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^everyone$/i })).not.toBeInTheDocument();

    const controls = screen.getByRole('group', { name: 'List controls' });
    const tabsLine = controls.parentElement!;
    expect(tabsLine).toContainElement(screen.getByRole('tablist', { name: 'Inquiry views' }));
    expect(within(controls).getByRole('button', { name: 'Simple view' })).toBeInTheDocument();
    expect(within(controls).getByRole('button', { name: 'Spreadsheet view' })).toBeInTheDocument();
    expect(within(controls).getByRole('button', { name: 'Display' })).toBeInTheDocument();
    // Clear filters shows only while a filter is on.
    expect(within(controls).queryByRole('button', { name: /clear filters/i })).not.toBeInTheDocument();

    pick('bid due date', 'Overdue');
    await waitFor(() => expect(lastGrid()?.due).toBe('overdue'));
    fireEvent.click(within(controls).getByRole('button', { name: /clear filters/i }));
    await waitFor(() => expect(lastGrid()?.due).toBeUndefined());
    // It clears what the reader narrowed and stays on the tab they are on.
    expect(lastGrid()?.view).toBe('revisions');
    expect(within(controls).queryByRole('button', { name: /clear filters/i })).not.toBeInTheDocument();
  });

  it('Clear filters on the tabs line drops every header filter and the search in one go', async () => {
    auth.userData = { id: 7, isManager: true };
    asSpreadsheet();
    renderPage('/procurement/leads/all?customer=30&serial=NOOR&owner=mine&status=none&itemsMin=11&itemsMax=100&dueFrom=2026-09-01&source=Email');
    await waitFor(() => expect(lastGrid()?.serial).toBe('NOOR'));
    fireEvent.click(within(screen.getByRole('group', { name: 'List controls' })).getByRole('button', { name: /clear filters/i }));
    await waitFor(() => expect(lastGrid()).toEqual({ view: 'queue', pageNumber: 1, pageSize: 25 }));
    expect(url.search).toBe('');
  });

  it('the Simple view puts "Came from" beside Customer in one header', async () => {
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    const header = screen.getAllByRole('columnheader').find((h) => h.textContent?.includes('Customer & bid'))!;
    fireEvent.click(within(header).getByRole('button', { name: 'Filter by customer' }));
    const cameFrom = screen.getByRole('menu', { name: 'Came from' });
    expect(within(cameFrom).getAllByRole('menuitem').map((o) => o.textContent)).toEqual(['Any source', 'Email', 'Manual', 'Bulk upload']);
    expect(screen.getByRole('menu', { name: 'Customer' })).toBeInTheDocument();
    fireEvent.click(within(cameFrom).getByRole('menuitem', { name: 'Bulk upload' }));
    await waitFor(() => expect(lastGrid()?.leadSource).toBe('Bulk'));
    expect(urlParams().get('source')).toBe('Bulk');
  });

  it('every spreadsheet column the server can filter has a header filter; computed columns have none', async () => {
    asSpreadsheet();
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    const header = (title: string) => screen.getAllByRole('columnheader').find((h) => h.textContent?.startsWith(title))!;
    const expected: Record<string, string> = {
      'Nexora Serial': 'serial', 'RFQ/Bid #': 'RFQ/Bid number', Client: 'customer', 'Buyer contact': 'buyer',
      Received: 'received date', Ingested: 'ingested date', 'Deadline (Hijri)': 'bid due date (Hijri)', 'Required delivery': 'required delivery',
      'Agreement reference': 'agreement', Items: 'items', Source: 'source', Status: 'status', Owner: 'owner',
    };
    Object.entries(expected).forEach(([title, noun]) => {
      expect(within(header(title)).getByRole('button', { name: `Filter by ${noun}` })).toBeInTheDocument();
    });
    const deadline = screen.getAllByRole('columnheader').find((h) => h.textContent === 'Deadline')!;
    expect(within(deadline).getByRole('button', { name: 'Filter by bid due date' })).toBeInTheDocument();
    ["Nexora's read", 'Estimated value'].forEach((title) => {
      expect(within(header(title)).queryByRole('button', { name: /^Filter by/ })).not.toBeInTheDocument();
    });
    expect(header('RFQ/Bid #')).toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: /^RFQ #/ })).not.toBeInTheDocument();
  });

  it.each([
    ['serial', 'serial', 'NOOR-59'],
    ['RFQ/Bid number', 'rfq', '6000000028'],
    ['buyer', 'buyer', 'ali@aramco.com'],
    ['agreement', 'agreement', 'AG-77'],
  ] as const)('the %s text filter sends %s, from page 1', async (noun, param, text) => {
    asSpreadsheet();
    renderPage();
    const next = await screen.findByRole('button', { name: /go to next page/i });
    await waitFor(() => expect(next).toBeEnabled());
    fireEvent.click(next);
    await waitFor(() => expect(lastGrid()?.pageNumber).toBe(2));

    typeFilter(noun, `  ${text} `);
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ [param]: text, pageNumber: 1 })));
    expect(urlParams().get(param)).toBe(text);
    expect(screen.getByRole('button', { name: `Filter by ${noun}` })).toHaveAttribute('aria-pressed', 'true');

    // Clear removes it.
    fireEvent.click(screen.getByRole('button', { name: `Filter by ${noun}` }));
    expect(screen.getByRole('textbox', { name: `${capital(noun)} contains` })).toHaveValue(text);
    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
    await waitFor(() => expect(lastGrid()?.[param]).toBeUndefined());
  });

  it('a custom Deadline range sends dueFrom/dueTo and no due window', async () => {
    renderPage('/procurement/leads/all?due=7d');
    await waitFor(() => expect(lastGrid()?.due).toBe('7d'));
    fireEvent.click(screen.getByRole('button', { name: 'Filter by bid due date' }));
    expect(within(screen.getByRole('menu')).getAllByRole('menuitem').map((o) => o.textContent))
      .toEqual(['Overdue', 'Next 7 days', 'Next 14 days', 'Custom range…', 'Any date']);
    fireEvent.click(screen.getByRole('menuitem', { name: 'Custom range…' }));
    // Typed back to front, it still means the days between.
    fireEvent.change(screen.getByLabelText('From'), { target: { value: '2026-10-31' } });
    fireEvent.change(screen.getByLabelText('To'), { target: { value: '2026-10-01' } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));

    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ dueFrom: '2026-10-01', dueTo: '2026-10-31' })));
    expect(lastGrid()?.due).toBeUndefined();
    expect(lastGrid()?.today).toBeUndefined();
    expect(urlParams().has('due')).toBe(false);
    expect(screen.getByRole('button', { name: 'Filter by bid due date' })).toHaveAttribute('aria-pressed', 'true');

    // A preset replaces the range.
    pick('bid due date', 'Overdue');
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ due: 'overdue', today: localToday() })));
    expect(lastGrid()?.dueFrom).toBeUndefined();
    expect(lastGrid()?.dueTo).toBeUndefined();
  });

  it('a Received preset sends startDate/endDate on the local calendar', async () => {
    asSpreadsheet();
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    pick('received date', 'Last 7 days');
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ startDate: localDay(-6), endDate: localToday() })));
    expect(urlParams().get('received')).toBe('7d');
    pick('received date', 'Today');
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ startDate: localToday(), endDate: localToday() })));
    pick('received date', 'Any date');
    await waitFor(() => expect(lastGrid()?.startDate).toBeUndefined());
  });

  it('an Ingested preset sends the reader\'s local midnights as instants, like the cell shows it', async () => {
    asSpreadsheet();
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    pick('ingested date', 'Last 7 days');
    const now = new Date();
    const midnight = (days: number) => new Date(now.getFullYear(), now.getMonth(), now.getDate() + days).toISOString();
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ ingestedFrom: midnight(-6), ingestedBefore: midnight(1) })));
    expect(lastGrid()?.startDate).toBeUndefined(); // not the Received date
    expect(urlParams().get('ingested')).toBe('7d');
    pick('ingested date', 'Any date');
    await waitFor(() => expect(lastGrid()?.ingestedFrom).toBeUndefined());
  });

  it('Required delivery "Next 30 days" sends requiredFrom/requiredTo', async () => {
    asSpreadsheet();
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    pick('required delivery', 'Next 30 days');
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ requiredFrom: localToday(), requiredTo: localDay(30) })));
  });

  it('an Items bucket sends itemsMin/itemsMax', async () => {
    asSpreadsheet();
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    fireEvent.click(screen.getByRole('button', { name: 'Filter by items' }));
    expect(within(screen.getByRole('menu')).getAllByRole('menuitem').map((o) => o.textContent)).toEqual(['1–10', '11–100', 'Over 100', 'Any']);
    fireEvent.click(screen.getByRole('menuitem', { name: '11–100' }));
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ itemsMin: 11, itemsMax: 100 })));
    pick('items', 'Over 100');
    await waitFor(() => expect(lastGrid()?.itemsMin).toBe(101));
    expect(lastGrid()?.itemsMax).toBeUndefined();
  });

  it('Status offers "Not opened yet" and each status in the shared words, and sends status=none within the queue', async () => {
    renderPage();
    await waitFor(() => expect(getListCustomers).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('button', { name: 'Filter by status' }));
    await waitFor(() => expect(within(screen.getByRole('menu')).getAllByRole('menuitem').map((o) => o.textContent))
      .toEqual(['Any status', 'Not opened yet (8)', 'Qualified (2)', 'On hold (1)']));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Not opened yet (8)' }));
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ status: 'none', view: 'queue' })));
    pick('status', 'Qualified');
    await waitFor(() => expect(lastGrid()?.status).toBe('5'));
  });

  it('search travels once, as search — never as rfqno', async () => {
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    expect(screen.getByPlaceholderText('Search by serial, RFQ/Bid number, buyer or email')).toBeInTheDocument();
    fireEvent.change(screen.getByPlaceholderText(/search by serial/i), { target: { value: '6000000028' } });
    await waitFor(() => expect(lastGrid()?.search).toBe('6000000028'));
    expect(calls().some((p) => 'rfqno' in p)).toBe(false);
  });

  it('Export to Excel sends the same parameters as the grid for every header filter', async () => {
    auth.userData = { id: 7, isManager: true };
    renderPage('/procurement/leads/all?serial=NOOR&rfq=600&buyer=ali&agreement=AG&dueFrom=2026-10-01&dueTo=2026-10-31'
      + '&received=30d&required=30d&itemsMin=1&itemsMax=10&status=none&source=Manual&owner=unassigned&customer=31');
    await waitFor(() => expect(lastGrid()?.serial).toBe('NOOR'));
    const grid = lastGrid()!;
    expect(grid).toEqual(expect.objectContaining({
      serial: 'NOOR', rfq: '600', buyer: 'ali', agreement: 'AG', dueFrom: '2026-10-01', dueTo: '2026-10-31',
      startDate: localDay(-29), endDate: localToday(), requiredFrom: localToday(), requiredTo: localDay(30),
      itemsMin: 1, itemsMax: 10, status: 'none', leadSource: 'Manual', view: 'queue,unassigned', customer: '31',
    }));

    fireEvent.click(screen.getByRole('button', { name: /export to excel/i }));
    await waitFor(() => expect(calls().some((p) => p.pageSize === 500)).toBe(true));
    const exported = calls().find((p) => p.pageSize === 500)!;
    const strip = ({ pageNumber: _n, pageSize: _s, ...rest }: Params) => rest;
    expect(strip(exported)).toEqual(strip(grid));
  });

  it('says "No inquiries match" with every new filter in words', async () => {
    getAll.mockImplementation((params: Params) => Promise.resolve(
      params.pageSize === 1
        ? { items: [], totalCount: 3, pageNumber: 1, pageSize: 1 }
        : { items: [], totalCount: 0, pageNumber: 1, pageSize: 25 },
    ));
    renderPage('/procurement/leads/all?source=Bulk&dueFrom=2026-10-01&dueTo=2026-10-31&received=7d&itemsMin=101&serial=NOOR&status=5');
    expect(await screen.findByText('No inquiries match')).toBeInTheDocument();
    expect(await screen.findByText(
      'Came from Bulk upload · Deadline 01 Oct 2026 – 31 Oct 2026 · Received last 7 days · Over 100 items · Serial contains "NOOR" · Qualified',
    )).toBeInTheDocument();
  });
});
