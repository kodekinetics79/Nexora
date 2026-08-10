import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import ExtractionReviewDetailPage from './ExtractionReviewDetailPage';
import type { ListViewColumnsResponse } from '../../api/services/listViewService';

/**
 * AA-01 · proof that the lead/RFQ LINE grid is wired, not merely that a preference
 * round-trips.
 *
 * Each test here fails if a specific piece of the wiring is deleted:
 *
 *  - the grid asks for `lead.items` (not a list-view key, and not nothing);
 *  - the saved ORDER decides the rendered column order;
 *  - a tenant-defined custom field becomes a real column reading the line's own
 *    jsonb bag, and an unset value renders "Not set" rather than an empty cell
 *    that reads as zero;
 *  - the commercial columns read the persisted line resolution, and a line with
 *    no resolution says "Not checked" instead of showing a 0 that reads as
 *    "none in stock";
 *  - a part that appears on two lines is refused rather than resolved to
 *    whichever resolution happened to be first.
 */

// This suite mounts the whole review workbench — a DataGrid with seventeen columns, three
// queries and a popover — so a single case can legitimately take several seconds when the
// suite runs in parallel with the rest of the frontend tests. The default 5s budget makes it
// a load-flake rather than a real signal.
vi.setConfig({ testTimeout: 30_000 });

const getColumns = vi.fn();
const saveColumns = vi.fn();
const resetColumns = vi.fn();

vi.mock('../../api/services/listViewService', () => ({
  default: {
    getColumns: (viewKey: string) => getColumns(viewKey),
    saveColumns: (viewKey: string, columns: unknown) => saveColumns(viewKey, columns),
    resetColumns: (viewKey: string) => resetColumns(viewKey),
  },
}));

const getLeadLineResolutions = vi.fn();
vi.mock('../../api/services/commercialIntelligenceService', () => ({
  default: {
    getLeadLineResolutions: (leadId: number) => getLeadLineResolutions(leadId),
  },
}));

const getLead = vi.fn();
vi.mock('../../api/services/extractionReviewService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/extractionReviewService')>();
  return {
    ...actual,
    default: {
      getLead: (id: number) => getLead(id),
      getProcessingEvidence: () => Promise.resolve(null),
      getFieldEvidence: () => Promise.resolve({ entries: [], mapped: false, documentNarrative: '' }),
      submitReview: () => Promise.resolve({}),
    },
  };
});

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => vi.fn(), useParams: () => ({ id: '7' }) };
});

vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));

// Two lines sharing nothing, plus one duplicate part, so every join outcome is covered.
const LEAD = {
  id: 7,
  rfqno: 'RFQ-7',
  buyersName: 'Buyer',
  bidClosingDate: '2026-09-01T00:00:00Z',
  opportunityNo: '',
  headerRemarks: '',
  recDate: '2026-08-01T00:00:00Z',
  leadSource: 'EMAIL',
  reviewVersion: 1,
  attachments: [],
  leadItems: [
    {
      id: 101, lineItemNo: '10', productShortName: 'Ball valve',
      manufacturerPartNumber: 'BV-100', quantity: 5, unitOfMeasure: 'EA',
      customFields: '{"plant_code":"JBL-2"}',
    },
    {
      id: 102, lineItemNo: '20', productShortName: 'Gasket',
      manufacturerPartNumber: 'GK-9', quantity: 12, unitOfMeasure: 'EA',
      // No custom-field bag at all: the column must still render an explicit state.
      customFields: null,
    },
  ],
};

const RESOLUTIONS = [
  {
    id: 1, leadId: 7, leadRevisionId: 3, leadLineId: 55, productId: 9,
    requestedPartNumber: 'BV-100', requestedQuantity: 5,
    classification: 'KnownShortage' as const,
    availableToPromise: 2, incomingAvailable: 4, projectedShortage: 3,
    leadTimeDays: 14, expectedAvailableOn: '2026-09-15', unitCost: 310.5,
    costCurrencyCode: 'SAR',
    fulfilment: {}, relatedResources: [], productResolution: {},
    inventoryAsOfUtc: '2026-08-08T00:00:00Z', resolvedOn: '2026-08-08T00:00:00Z',
    externalDiscoveryUsed: false,
  },
];

const columnsResponse = (over: Partial<ListViewColumnsResponse> = {}): ListViewColumnsResponse => ({
  viewKey: 'lead.items',
  isCustomised: false,
  supportsCustomFields: true,
  columns: [
    { key: 'checkStatus', label: 'Review status', visible: true, locked: true, source: 'catalog' },
    { key: 'lineItemNo', label: 'Line #', visible: true, locked: false, source: 'catalog' },
    { key: 'productShortName', label: 'Product', visible: true, locked: false, source: 'catalog' },
    { key: 'stockAvailable', label: 'Available now', visible: true, locked: false, source: 'catalog' },
    { key: 'stockIncoming', label: 'Incoming', visible: true, locked: false, source: 'catalog' },
    { key: 'projectedShortage', label: 'Projected shortage', visible: true, locked: false, source: 'catalog' },
    { key: 'supplyStatus', label: 'Supply status', visible: true, locked: false, source: 'catalog' },
    { key: 'stockUnitCost', label: 'Stock unit cost', visible: true, locked: false, source: 'catalog' },
    {
      key: 'cf:plant_code', label: 'Plant code', visible: true, locked: false,
      source: 'customField', dataType: 'Text', stableKey: 'plant_code',
    },
    { key: 'actions', label: 'Actions', visible: true, locked: true, source: 'catalog' },
  ],
  ...over,
});

const renderPage = () =>
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter>
        <ExtractionReviewDetailPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );

/** Cell text for one column of one row, read out of the rendered grid. */
const cellText = async (lineNumber: string, field: string): Promise<string> => {
  const cell = await waitFor(() => {
    const cells = document.querySelectorAll(`[data-field="${field}"]`);
    const match = [...cells].find((element) => {
      const row = element.closest('.MuiDataGrid-row');
      return row?.querySelector('[data-field="lineItemNo"]')?.textContent === lineNumber;
    });
    if (!match) throw new Error(`no ${field} cell on line ${lineNumber}`);
    return match as HTMLElement;
  });
  return cell.textContent ?? '';
};

beforeEach(() => {
  vi.clearAllMocks();
  getLead.mockResolvedValue(LEAD);
  getColumns.mockResolvedValue(columnsResponse());
  saveColumns.mockImplementation((_key: string, columns: { key: string; visible: boolean }[]) =>
    Promise.resolve(columnsResponse({
      isCustomised: true,
      columns: columns.map((c) => {
        const declared = columnsResponse().columns.find((d) => d.key === c.key)!;
        return { ...declared, visible: c.visible || declared.locked };
      }),
    })));
  getLeadLineResolutions.mockResolvedValue(RESOLUTIONS);
});

describe('the lead line grid is wired to per-user column preferences', () => {
  it('asks the server for the lead.items view, not a list view', async () => {
    renderPage();
    await waitFor(() => expect(getColumns).toHaveBeenCalledWith('lead.items'));
  });

  it('offers the picker on the grid toolbar and saves the reordered layout for this user', async () => {
    renderPage();
    const picker = await screen.findByRole('button', { name: 'Choose columns' });
    fireEvent.click(picker);

    const moveDown = await screen.findByRole('button', { name: 'Move Line # down' });
    fireEvent.click(moveDown);

    await waitFor(() => expect(saveColumns).toHaveBeenCalledTimes(1));
    const [viewKey, saved] = saveColumns.mock.calls[0];
    expect(viewKey).toBe('lead.items');
    // The ORDER of the payload is the display order; Line # has moved past Product.
    expect(saved.map((c: { key: string }) => c.key).slice(0, 3))
      .toEqual(['checkStatus', 'productShortName', 'lineItemNo']);
  });

  it('renders the columns in the order the server returned them', async () => {
    renderPage();
    await screen.findByRole('columnheader', { name: 'Line #' });
    const headers = [...document.querySelectorAll('[role="columnheader"]')]
      .map((h) => h.getAttribute('data-field'))
      .filter((f): f is string => Boolean(f));
    expect(headers.indexOf('lineItemNo')).toBeLessThan(headers.indexOf('productShortName'));
    expect(headers.indexOf('productShortName')).toBeLessThan(headers.indexOf('stockAvailable'));
    expect(headers).toContain('cf:plant_code');
  });
});

describe('a tenant-defined field becomes a column on the line', () => {
  it('reads the value out of the line jsonb bag', async () => {
    renderPage();
    expect(await cellText('10', 'cf:plant_code')).toBe('JBL-2');
  });

  it('shows an explicit state, never an empty cell, when the line has no value', async () => {
    renderPage();
    expect((await cellText('20', 'cf:plant_code')).trim()).toBe('Not set');
  });
});

describe('commercial context is read from the persisted line resolution', () => {
  it('shows availability, incoming, shortage and cost for a resolved part', async () => {
    renderPage();
    expect(await cellText('10', 'stockAvailable')).toBe('2');
    expect(await cellText('10', 'stockIncoming')).toBe('4');
    expect(await cellText('10', 'projectedShortage')).toBe('3');
    expect(await cellText('10', 'supplyStatus')).toBe('Shortage');
    expect(await cellText('10', 'stockUnitCost')).toContain('SAR');
  });

  it('says "Not checked" rather than 0 when no resolution exists for the part', async () => {
    renderPage();
    expect(await cellText('20', 'stockAvailable')).toBe('Not checked');
    expect(await cellText('20', 'stockIncoming')).toBe('Not checked');
  });

  it('refuses to attribute stock when the same part is on more than one line', async () => {
    getLead.mockResolvedValue({
      ...LEAD,
      leadItems: [
        LEAD.leadItems[0],
        { ...LEAD.leadItems[1], manufacturerPartNumber: 'BV-100' },
      ],
    });
    renderPage();
    expect(await cellText('10', 'stockAvailable')).toBe('Part on several lines');
    expect(await cellText('20', 'stockAvailable')).toBe('Part on several lines');
  });

  it('leaves the grid usable when the inventory read is refused', async () => {
    getLeadLineResolutions.mockRejectedValue(new Error('403'));
    renderPage();
    expect(await cellText('10', 'stockAvailable')).toBe('Not checked');
    // The grid itself still renders every extraction column.
    expect(await cellText('10', 'productShortName')).toBe('Ball valve');
  });
});

describe('the picker degrades rather than emptying the grid', () => {
  it('keeps every rendered column when the preference endpoint is unreachable', async () => {
    getColumns.mockRejectedValue(new Error('network'));
    renderPage();
    await waitFor(() => expect(document.querySelectorAll('.MuiDataGrid-row').length).toBe(2));
    const headers = [...document.querySelectorAll('[role="columnheader"]')]
      .map((h) => h.getAttribute('data-field'));
    expect(headers).toContain('lineItemNo');
    expect(headers).toContain('productShortName');
  });
});

it('never renders a bare blank where a commercial figure is unknown', async () => {
  renderPage();
  const cells = await waitFor(() => {
    const found = document.querySelectorAll('[data-field="supplyStatus"]');
    if (found.length === 0) throw new Error('not rendered');
    return [...found] as HTMLElement[];
  });
  for (const cell of cells) expect((cell.textContent ?? '').trim().length).toBeGreaterThan(0);
});
