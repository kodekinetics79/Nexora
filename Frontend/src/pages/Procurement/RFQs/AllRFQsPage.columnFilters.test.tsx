import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

// Drives the full DataGrid page, often in the wide spreadsheet view, through jsdom.
vi.setConfig({ testTimeout: 30_000 });

/**
 * The RFQs list filters, all in the column headers, like the Leads list (owner 2026-09-30: "move on
 * to RFQ"). The list on screen, the list in Export to Excel and the URL all say the same thing, and
 * the old filter row (Client picker, Unassigned/Mine/Everyone) is gone, with the view controls on
 * the tabs line.
 */

const getAll = vi.fn();
const getListChoices = vi.fn();
const getOwnerOptions = vi.fn();
const auth = vi.hoisted(() => ({ userData: {} as Record<string, unknown> }));

vi.mock('../../../api/services/rfqService', () => ({
  default: {
    getAll: (params: unknown) => getAll(params),
    getListChoices: (readiness: unknown) => getListChoices(readiness),
  },
}));
vi.mock('../../../api/services/commercialRoutingService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/commercialRoutingService')>();
  return { ...actual, default: { getOwnerOptions: () => getOwnerOptions() } };
});
vi.mock('../../../hooks/useColumnPreferences', () => ({
  default: () => ({ columnVisibilityModel: {}, onColumnVisibilityModelChange: vi.fn(), arrangeColumns: <T,>(defs: T) => defs, isLoading: false, isError: false }),
}));
vi.mock('../../../components/common/ColumnPreferences', () => ({ default: () => null }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true, userData: auth.userData }),
}));
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));
vi.mock('xlsx', async (importOriginal) => {
  const actual = await importOriginal<typeof import('xlsx')>();
  return { ...actual, writeFile: vi.fn() };
});

import AllRFQsPage from './AllRFQsPage';

type Params = Record<string, unknown> & { pageSize?: number; pageNumber?: number };
const calls = (): Params[] => getAll.mock.calls.map((call) => call[0] as Params);
/** The grid's requests: pageSize 1 is the unfiltered total, 500 is Export to Excel. */
const gridCalls = (): Params[] => calls().filter((p) => p.pageSize !== 1 && p.pageSize !== 500);
const lastGrid = (): Params | undefined => gridCalls().at(-1);
const strip = ({ pageNumber: _n, pageSize: _s, ...rest }: Params) => rest;

const localDay = (offset = 0) => {
  const now = new Date();
  const day = new Date(now.getFullYear(), now.getMonth(), now.getDate() + offset);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${day.getFullYear()}-${pad(day.getMonth() + 1)}-${pad(day.getDate())}`;
};
const localToday = () => localDay(0);

const url = { search: '' };
const LocationSpy = () => {
  url.search = useLocation().search;
  return null;
};
const urlParams = () => new URLSearchParams(url.search);

const renderPage = (route = '/procurement/rfqs/all') => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <MemoryRouter initialEntries={[route]}>
      <LocationSpy />
      <QueryClientProvider client={client}>
        <AllRFQsPage />
      </QueryClientProvider>
    </MemoryRouter>,
  );
};

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&');

/** Open a column header's filter and choose one entry. */
const pick = (noun: string, entry: string) => {
  fireEvent.click(screen.getByRole('button', { name: `Filter by ${noun}` }));
  fireEvent.click(within(screen.getByRole('menu')).getByRole('menuitem', { name: new RegExp(`^${escape(entry)}`) }));
};
const menuWords = () => within(screen.getByRole('menu')).getAllByRole('menuitem').map((o) => o.textContent);

/** Custom range: open the header, choose "Custom range…", type the days, apply. */
const customRange = (noun: string, from: string, to: string) => {
  fireEvent.click(screen.getByRole('button', { name: `Filter by ${noun}` }));
  fireEvent.click(screen.getByRole('menuitem', { name: 'Custom range…' }));
  fireEvent.change(screen.getByLabelText('From'), { target: { value: from } });
  fireEvent.change(screen.getByLabelText('To'), { target: { value: to } });
  fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
};

const asSpreadsheet = () => localStorage.setItem('nexora.rfqsPage.view:global', 'spreadsheet');

const header = (title: string) => screen.getAllByRole('columnheader').find((h) => h.textContent?.startsWith(title))!;

const rows = Array.from({ length: 25 }, (_, i) => ({ id: i + 1, rfqno: `RFQ-${i + 1}`, rfqitems: [], readiness: '' }));

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  url.search = '';
  auth.userData = { id: 7, businessUnitId: 1 };
  getAll.mockImplementation((params: Params) => Promise.resolve(
    params.pageSize === 1
      ? { items: [], totalItems: 60, pageNumber: 1, pageSize: 1, totalPages: 60 }
      : { items: params.pageSize === 500 ? rows.slice(0, 2) : rows, totalItems: 60, pageNumber: params.pageNumber ?? 1, pageSize: params.pageSize ?? 25, totalPages: 3 },
  ));
  getListChoices.mockResolvedValue({
    customers: [
      { value: '30', label: 'Saudi Electricity Company', count: 12 },
      { value: '31', label: 'Marafiq', count: 4 },
    ],
    noCustomer: 3,
    statuses: [{ value: '2', label: 'Open', count: 5 }, { value: '4', label: 'Closed', count: 1 }],
    rfqTypes: [{ value: 'Tender', label: 'Tender', count: 3 }],
    inquiryTypes: [{ value: 'Spot', label: 'Spot', count: 2 }],
    biddingDecisions: [{ value: 'Bid', label: 'Bid', count: 6 }],
  });
  getOwnerOptions.mockResolvedValue([
    { userId: 42, name: 'Omar Rep', email: 'omar@x', isAvailable: true, acceptsManualAssignment: true, capacityPercent: 10, eligibilityReason: '' },
  ]);
});

describe('AllRFQsPage — layout', () => {
  it('has no filter row: Simple/Spreadsheet, Display and Clear filters sit on the tabs line', async () => {
    renderPage('/procurement/rfqs/all?state=ready-for-quote');
    await waitFor(() => expect(lastGrid()).toBeDefined());
    expect(screen.queryByLabelText('Client')).not.toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Owner' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^everyone$/i })).not.toBeInTheDocument();

    const controls = screen.getByRole('group', { name: 'List controls' });
    expect(controls.parentElement!).toContainElement(screen.getByRole('tablist', { name: 'RFQ views' }));
    expect(within(controls).getByRole('button', { name: 'Simple view' })).toBeInTheDocument();
    expect(within(controls).getByRole('button', { name: 'Spreadsheet view' })).toBeInTheDocument();
    expect(within(controls).getByRole('button', { name: 'Display' })).toBeInTheDocument();
    expect(within(controls).queryByRole('button', { name: /clear filters/i })).not.toBeInTheDocument();

    pick('deadline', 'Overdue');
    await waitFor(() => expect(lastGrid()?.due).toBe('overdue'));
    fireEvent.click(within(controls).getByRole('button', { name: /clear filters/i }));
    await waitFor(() => expect(lastGrid()?.due).toBeUndefined());
    // It clears what the reader narrowed and stays on the tab they are on.
    expect(lastGrid()?.readiness).toBe('ready-for-quote');
    expect(within(controls).queryByRole('button', { name: /clear filters/i })).not.toBeInTheDocument();
  });

  it('Clear filters also shows for a search, and drops every header filter and the search in one go', async () => {
    asSpreadsheet();
    auth.userData = { id: 7, businessUnitId: 1, isManager: true };
    renderPage('/procurement/rfqs/all?customer=30&serial=NOOR&owner=mine&statusId=2&linesMin=11&linesMax=100&dueFrom=2026-09-01&quote=draft&bidding=Bid&created=7d');
    await waitFor(() => expect(lastGrid()?.serial).toBe('NOOR'));
    fireEvent.click(within(screen.getByRole('group', { name: 'List controls' })).getByRole('button', { name: /clear filters/i }));
    await waitFor(() => expect(lastGrid()).toEqual({ businessUnitId: 1, readiness: 'open', pageNumber: 1, pageSize: 25 }));
    expect(url.search).toBe('');

    fireEvent.change(screen.getByPlaceholderText('Search RFQ/Bid number, serial, customer or buyer'), { target: { value: 'RFQ-1' } });
    fireEvent.click(await within(screen.getByRole('group', { name: 'List controls' })).findByRole('button', { name: /clear filters/i }));
    expect(screen.getByPlaceholderText('Search RFQ/Bid number, serial, customer or buyer')).toHaveValue('');
  });

  it('heads the RFQ number column "RFQ/Bid #" in both views', async () => {
    const { unmount } = renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    expect(within(header('RFQ/Bid #')).getByRole('button', { name: 'Filter by RFQ/Bid number' })).toBeInTheDocument();
    expect(screen.queryByText('rfq_number')).not.toBeInTheDocument();
    unmount();

    asSpreadsheet();
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    expect(header('RFQ/Bid #')).toBeInTheDocument();
    expect(screen.queryByText('rfq_number')).not.toBeInTheDocument();
  });

  it('every spreadsheet column the server can filter has a header filter; Contact and Readiness have none', async () => {
    asSpreadsheet();
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    const expected: Record<string, string> = {
      'Nexora serial': 'serial', 'RFQ/Bid #': 'RFQ/Bid number', Customer: 'customer', Owner: 'owner', Lines: 'lines',
      Deadline: 'deadline', 'Deadline (Hijri)': 'deadline (Hijri)', Quote: 'quote',
      'Customer RFQ reference': 'customer RFQ reference', 'Customer email': 'customer email', Buyer: 'buyer',
      'Account owner': 'account owner', Received: 'received date', 'Required delivery': 'required delivery',
      'Delivery location': 'delivery location', 'Agreement reference': 'agreement reference', 'Opportunity #': 'opportunity number',
      'RFQ type': 'RFQ type', 'Inquiry type': 'inquiry type', 'Bidding decision': 'bidding decision', Submitted: 'submitted date',
      Status: 'status', 'Made from lead by': 'made from lead by', Created: 'created date', Modified: 'modified date',
    };
    Object.entries(expected).forEach(([title, noun]) => {
      const cell = screen.getAllByRole('columnheader').find((h) => h.textContent === title);
      expect(cell, title).toBeDefined();
      expect(within(cell!).getByRole('button', { name: `Filter by ${noun}` })).toBeInTheDocument();
    });
    ['Contact', 'Readiness'].forEach((title) => {
      expect(within(header(title)).queryByRole('button', { name: /^Filter by/ })).not.toBeInTheDocument();
    });
  });
});

describe('AllRFQsPage — header filters', () => {
  it('"Next 7 days" in the Deadline header sends due 7d with today, from page 1', async () => {
    renderPage();
    const next = await screen.findByRole('button', { name: /go to next page/i });
    await waitFor(() => expect(next).toBeEnabled());
    fireEvent.click(next);
    await waitFor(() => expect(lastGrid()?.pageNumber).toBe(2));

    fireEvent.click(screen.getByRole('button', { name: 'Filter by deadline' }));
    expect(menuWords()).toEqual(['Overdue', 'Next 7 days', 'Next 14 days', 'Custom range…', 'Any date']);
    fireEvent.click(screen.getByRole('menuitem', { name: 'Next 7 days' }));
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ due: '7d', today: localToday(), pageNumber: 1 })));
    // Never page 2 of the narrower list, not even for one request.
    expect(gridCalls().filter((p) => p.due === '7d').every((p) => p.pageNumber === 1)).toBe(true);
    expect(urlParams().get('due')).toBe('7d');
    expect(screen.getByRole('button', { name: 'Filter by deadline' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('a custom Deadline range sends dueFrom/dueTo and no due window', async () => {
    renderPage('/procurement/rfqs/all?due=7d');
    await waitFor(() => expect(lastGrid()?.due).toBe('7d'));
    // Typed back to front, it still means the days between.
    customRange('deadline', '2026-10-31', '2026-10-01');
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ dueFrom: '2026-10-01', dueTo: '2026-10-31' })));
    expect(lastGrid()?.due).toBeUndefined();
    expect(lastGrid()?.today).toBeUndefined();
    expect(urlParams().has('due')).toBe(false);
  });

  it('the Hijri deadline column shares the same filter', async () => {
    asSpreadsheet();
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    pick('deadline (Hijri)', 'Overdue');
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ due: 'overdue', today: localToday() })));
    expect(screen.getByRole('button', { name: 'Filter by deadline' })).toHaveAttribute('aria-pressed', 'true');
  });

  it("the Customer header offers this list's customers with counts and No customer yet, and sends customer, never customerId", async () => {
    renderPage();
    await waitFor(() => expect(getListChoices).toHaveBeenCalledWith('open'));
    fireEvent.click(screen.getByRole('button', { name: 'Filter by customer' }));
    await waitFor(() => expect(menuWords()).toEqual(['Any customer', 'Saudi Electricity Company (12)', 'Marafiq (4)', 'No customer yet (3)']));
    fireEvent.change(screen.getByRole('textbox', { name: 'Find a customer' }), { target: { value: 'mara' } });
    expect(menuWords()).toEqual(['Any customer', 'Marafiq (4)']);
    fireEvent.click(screen.getByRole('menuitem', { name: 'Marafiq (4)' }));
    await waitFor(() => expect(lastGrid()?.customer).toBe('31'));
    expect(calls().some((p) => 'customerId' in p)).toBe(false);

    pick('customer', 'No customer yet');
    await waitFor(() => expect(lastGrid()?.customer).toBe('none'));
  });

  it.each([
    ['status', 'Any status', ['Open (5)', 'Closed (1)'], 'Closed (1)', 'statusId', 4],
    ['RFQ type', 'Any', ['Tender (3)'], 'Tender (3)', 'rfqType', 'Tender'],
    ['inquiry type', 'Any', ['Spot (2)'], 'Spot (2)', 'inquiryType', 'Spot'],
    ['bidding decision', 'Any', ['Bid (6)'], 'Bid (6)', 'bidding', 'Bid'],
  ] as const)('the %s header offers the choices with counts and sends %s', async (noun, anyLabel, options, choice, param, sent) => {
    asSpreadsheet();
    renderPage();
    await waitFor(() => expect(getListChoices).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('button', { name: `Filter by ${noun}` }));
    await waitFor(() => expect(menuWords()).toEqual([anyLabel, ...options]));
    fireEvent.click(screen.getByRole('menuitem', { name: choice }));
    await waitFor(() => expect(lastGrid()?.[param]).toBe(sent));
    expect(screen.getByRole('button', { name: `Filter by ${noun}` })).toHaveAttribute('aria-pressed', 'true');
  });

  it.each([
    ['RFQ/Bid number', 'rfq', '6000000028'],
    ['serial', 'serial', 'NOOR-59'],
    ['customer RFQ reference', 'customerRef', 'CR-1'],
    ['customer email', 'email', 'ali@aramco.com'],
    ['buyer', 'buyer', 'Ali'],
    ['account owner', 'accountOwner', 'Sara'],
    ['delivery location', 'location', 'Dhahran'],
    ['agreement reference', 'agreement', 'AG-77'],
    ['opportunity number', 'opportunity', 'OPP-9'],
    ['made from lead by', 'promotedBy', 'omar'],
  ] as const)('the %s text filter sends %s', async (noun, param, text) => {
    asSpreadsheet();
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    const cap = noun.charAt(0).toUpperCase() + noun.slice(1);
    fireEvent.click(screen.getByRole('button', { name: `Filter by ${noun}` }));
    fireEvent.change(screen.getByRole('textbox', { name: `${/^[A-Z]{2}/.test(noun) ? noun : cap} contains` }), { target: { value: `  ${text} ` } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ [param]: text, pageNumber: 1 })));
    expect(urlParams().get(param)).toBe(text);
  });

  it('a Lines bucket sends linesMin/linesMax', async () => {
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    fireEvent.click(screen.getByRole('button', { name: 'Filter by lines' }));
    expect(menuWords()).toEqual(['1–10', '11–100', 'Over 100', 'Any']);
    fireEvent.click(screen.getByRole('menuitem', { name: '11–100' }));
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ linesMin: 11, linesMax: 100 })));
    pick('lines', 'Over 100');
    await waitFor(() => expect(lastGrid()?.linesMin).toBe(101));
    expect(lastGrid()?.linesMax).toBeUndefined();
    pick('lines', 'Any');
    await waitFor(() => expect(lastGrid()?.linesMin).toBeUndefined());
  });

  it('Owner is Anyone · Unassigned · Mine, then each rep for a manager — unassigned or assignedToId on the server', async () => {
    auth.userData = { id: 7, businessUnitId: 1, isManager: true };
    renderPage();
    await waitFor(() => expect(getOwnerOptions).toHaveBeenCalled());
    await waitFor(() => expect(lastGrid()).toBeDefined());
    expect(lastGrid()?.unassigned).toBeUndefined();
    expect(lastGrid()?.assignedToId).toBeUndefined();

    fireEvent.click(screen.getByRole('button', { name: 'Filter by owner' }));
    await waitFor(() => expect(menuWords()).toEqual(['Anyone', 'Unassigned', 'Mine', 'Omar Rep']));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Unassigned' }));
    await waitFor(() => expect(lastGrid()?.unassigned).toBe(true));
    expect(lastGrid()?.assignedToId).toBeUndefined();

    pick('owner', 'Mine');
    await waitFor(() => expect(lastGrid()?.assignedToId).toBe(7));
    expect(lastGrid()?.unassigned).toBeUndefined();

    pick('owner', 'Omar Rep');
    await waitFor(() => expect(lastGrid()?.assignedToId).toBe(42));
    expect(urlParams().get('rep')).toBe('42');
    expect(urlParams().has('owner')).toBe(false);

    pick('owner', 'Anyone');
    await waitFor(() => expect(lastGrid()?.assignedToId).toBeUndefined());
    expect(screen.getByRole('button', { name: 'Filter by owner' })).toHaveAttribute('aria-pressed', 'false');
  });

  it('does not offer reps to a non-manager, and ignores rep= on their URL', async () => {
    renderPage('/procurement/rfqs/all?rep=42');
    await waitFor(() => expect(lastGrid()).toBeDefined());
    expect(lastGrid()?.assignedToId).toBeUndefined();
    expect(getOwnerOptions).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Filter by owner' }));
    expect(menuWords()).toEqual(['Anyone', 'Unassigned', 'Mine']);
  });

  it('Quote "Sent" sends quote=sent and no readiness; the other quote states keep the open list', async () => {
    renderPage();
    await waitFor(() => expect(lastGrid()?.readiness).toBe('open'));
    fireEvent.click(screen.getByRole('button', { name: 'Filter by quote' }));
    expect(menuWords()).toEqual(['Any', 'Not quoted', 'Not sent yet', 'Sent']);
    fireEvent.click(screen.getByRole('menuitem', { name: 'Sent' }));
    await waitFor(() => expect(lastGrid()?.quote).toBe('sent'));
    expect(lastGrid()).not.toHaveProperty('readiness');
    // The choices are counted over the same RFQs.
    await waitFor(() => expect(getListChoices).toHaveBeenLastCalledWith(undefined));

    pick('quote', 'Not sent yet');
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ quote: 'draft', readiness: 'open' })));
    pick('quote', 'Not quoted');
    await waitFor(() => expect(lastGrid()?.quote).toBe('none'));
  });

  it.each([
    ['received date', 'received'],
    ['submitted date', 'submitted'],
    ['created date', 'created'],
    ['modified date', 'modified'],
  ] as const)('a %s preset and a custom range send %sFrom/%sTo local days', async (noun, key) => {
    asSpreadsheet();
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    fireEvent.click(screen.getByRole('button', { name: `Filter by ${noun}` }));
    expect(menuWords()).toEqual(['Today', 'Last 7 days', 'Last 30 days', 'Custom range…', 'Any date']);
    fireEvent.click(screen.getByRole('menuitem', { name: 'Last 7 days' }));
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ [`${key}From`]: localDay(-6), [`${key}To`]: localToday() })));
    expect(urlParams().get(key)).toBe('7d');

    customRange(noun, '2026-09-01', '2026-09-15');
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ [`${key}From`]: '2026-09-01', [`${key}To`]: '2026-09-15' })));
    expect(urlParams().has(key)).toBe(false);

    pick(noun, 'Any date');
    await waitFor(() => expect(lastGrid()?.[`${key}From`]).toBeUndefined());
  });

  it('Required delivery "Next 30 days" sends requiredFrom/requiredTo', async () => {
    asSpreadsheet();
    renderPage();
    await waitFor(() => expect(lastGrid()).toBeDefined());
    pick('required delivery', 'Next 30 days');
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ requiredFrom: localToday(), requiredTo: localDay(30) })));
  });

  it('Simple and Spreadsheet share one filter state', async () => {
    renderPage('/procurement/rfqs/all?serial=NOOR');
    await waitFor(() => expect(lastGrid()?.serial).toBe('NOOR'));
    expect(screen.getByRole('button', { name: 'Filter by serial' })).toHaveAttribute('aria-pressed', 'true');
    fireEvent.click(screen.getByRole('button', { name: 'Spreadsheet view' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Filter by customer email' })).toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Filter by serial' })).toHaveAttribute('aria-pressed', 'true');
    expect(lastGrid()?.serial).toBe('NOOR');
  });

  it('Export to Excel sends the same parameters as the grid for every header filter', async () => {
    auth.userData = { id: 7, businessUnitId: 1, isManager: true };
    renderPage('/procurement/rfqs/all?customer=31&rfq=600&serial=NOOR&customerRef=CR&email=ali&buyer=Ali&accountOwner=Sara'
      + '&location=Dhahran&agreement=AG&opportunity=OPP&promotedBy=omar&linesMin=1&linesMax=10&owner=unassigned&quote=draft'
      + '&statusId=2&rfqType=Tender&inquiryType=Spot&bidding=Bid&dueFrom=2026-10-01&dueTo=2026-10-31&received=30d&required=30d'
      + '&submittedFrom=2026-09-01&created=today&modifiedTo=2026-09-30');
    await waitFor(() => expect(lastGrid()?.serial).toBe('NOOR'));
    const grid = lastGrid()!;
    expect(grid).toEqual(expect.objectContaining({
      customer: '31', rfq: '600', serial: 'NOOR', customerRef: 'CR', email: 'ali', buyer: 'Ali', accountOwner: 'Sara',
      location: 'Dhahran', agreement: 'AG', opportunity: 'OPP', promotedBy: 'omar', linesMin: 1, linesMax: 10,
      unassigned: true, quote: 'draft', statusId: 2, rfqType: 'Tender', inquiryType: 'Spot', bidding: 'Bid',
      dueFrom: '2026-10-01', dueTo: '2026-10-31', receivedFrom: localDay(-29), receivedTo: localToday(),
      requiredFrom: localToday(), requiredTo: localDay(30), submittedFrom: '2026-09-01', createdFrom: localToday(),
      createdTo: localToday(), modifiedTo: '2026-09-30', readiness: 'open', businessUnitId: 1,
    }));

    fireEvent.click(screen.getByRole('button', { name: /export to excel/i }));
    await waitFor(() => expect(calls().some((p) => p.pageSize === 500)).toBe(true));
    const exported = calls().find((p) => p.pageSize === 500)!;
    expect(strip(exported)).toEqual(strip(grid));
  });

  it('says "No RFQs match" with the filters in words, and Clear filters drops them', async () => {
    getAll.mockImplementation((params: Params) => Promise.resolve(
      params.pageSize === 1
        ? { items: [], totalItems: 3, pageNumber: 1, pageSize: 1, totalPages: 3 }
        : { items: [], totalItems: 0, pageNumber: 1, pageSize: 25, totalPages: 0 },
    ));
    renderPage('/procurement/rfqs/all?customer=30&due=overdue&quote=draft&linesMin=101&serial=NOOR&owner=mine');
    expect(await screen.findByText('No RFQs match')).toBeInTheDocument();
    expect(await screen.findByText(
      'Saudi Electricity Company · Overdue · Over 100 lines · Serial contains "NOOR" · Quote not sent yet · Mine',
    )).toBeInTheDocument();

    fireEvent.click(screen.getAllByRole('button', { name: /clear filters/i }).at(-1)!);
    await waitFor(() => expect(lastGrid()).toEqual({ businessUnitId: 1, readiness: 'open', pageNumber: 1, pageSize: 25 }));
    expect(await screen.findByText('Nothing to quote')).toBeInTheDocument();
  });
});
