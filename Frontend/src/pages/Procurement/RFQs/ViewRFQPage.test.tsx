import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import ViewRFQPage, { presentRfqExtraFields } from './ViewRFQPage';
import type { RfqResponseDTO, RfqitemResponseDTO } from '../../../api/services/rfqService';
import type { RfqCommercialIntelligence } from '../../../api/services/commercialLearningService';
import type { SourcingWorkbench } from '../../../api/services/procurementService';

const getRfq = vi.fn();
const getWorkbench = vi.fn();
const getRfqIntelligence = vi.fn();
const getRfqLineResolutions = vi.fn();
const resolveLineProduct = vi.fn();
const createProduct = vi.fn();
const getProductById = vi.fn();
const getProducts = vi.fn();
const createOrOpenSourcingCase = vi.fn();
const testAccess = vi.hoisted(() => ({
  navigate: vi.fn(),
  denied: new Set<string>(),
}));

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => testAccess.navigate, useParams: () => ({ id: '9001' }) };
});
vi.mock('../../../api/services/rfqService', () => ({
  default: {
    getById: (...args: unknown[]) => getRfq(...args),
    approve: vi.fn(),
    prepareQuoteDraft: vi.fn(),
    resolveLineProduct: (...args: unknown[]) => resolveLineProduct(...args),
  },
}));
vi.mock('../../../api/services/productService', () => ({
  default: {
    getAll: (...args: unknown[]) => getProducts(...args),
    create: (...args: unknown[]) => createProduct(...args),
    getById: (...args: unknown[]) => getProductById(...args),
  },
}));
vi.mock('../../../api/services/procurementService', () => ({
  default: {
    getWorkbench: (...a: unknown[]) => getWorkbench(...a),
    createOrOpenSourcingCase: (...a: unknown[]) => createOrOpenSourcingCase(...a),
  },
}));
vi.mock('../../../api/services/commercialLearningService', () => ({
  default: { getRfqIntelligence: (...a: unknown[]) => getRfqIntelligence(...a) },
}));
vi.mock('../../../api/services/commercialIntelligenceService', () => ({
  default: { getRfqLineResolutions: (...a: unknown[]) => getRfqLineResolutions(...a) },
}));
vi.mock('../../../api/services/commercialLifecycleService', () => ({
  default: { getState: vi.fn().mockResolvedValue({ aggregateId: 9001, currentStatusCode: 'APPROVED', version: 1, isTerminal: false, allowedTransitions: [] }) },
}));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    hasPermission: (moduleName: string, action = 'view') => !testAccess.denied.has(`${moduleName}:${action}`),
    userData: { businessUnitId: 7, userName: 'qa', id: 1 },
  }),
}));
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));
// Panels with their own data of their own; not what this spec is about.
vi.mock('../../../components/common/CommercialLineIntelligence', () => ({ default: () => null }));
vi.mock('../../../components/common/CommercialProcessingEvidence', () => ({ default: () => null }));
vi.mock('../../../components/common/LifecycleActions', () => ({ default: () => null }));
vi.mock('../../../components/common/EmailPromptDialog', () => ({ default: () => null }));

const line = (id: number, over: Partial<RfqitemResponseDTO> = {}): RfqitemResponseDTO => ({
  id,
  rfqid: 9001,
  lineItemNo: `00${id}0`,
  quantity: 10,
  unitOfMeasure: 'EA',
  manufacturerPartNumber: `MPN-${id}`,
  productShortDescription: `Line ${id}`,
  bidClosingDateLine: '2026-09-01T00:00:00Z',
  createdBy: 'seed',
  createdDate: '2026-08-01T00:00:00Z',
  participationDecision: 'Pending',
  ...over,
});

/**
 * `recDate` and `createdDate` are non-nullable DateTime server-side, so an RFQ that never
 * captured one serialises as 0001-01-01 rather than null — the sentinel utils/dates.ts exists
 * to catch.
 */
const rfq = (over: Partial<RfqResponseDTO> = {}): RfqResponseDTO => ({
  id: 9001,
  rfqno: 'RFQ-9001',
  nexoraSerial: 'NX-9001',
  recDate: '2026-08-01T00:00:00Z',
  activeLeadRevision: 1,
  createdBy: 'qa',
  createdDate: '2026-08-01T00:00:00Z',
  businessUnitId: 7,
  rfqstatusValue: 'Approved',
  customerName: 'Fulton County',
  leadId: 55,
  readiness: 'Review Required',
  rfqitems: [line(1), line(2), line(3)],
  ...over,
});

const workbench = (lines: RfqitemResponseDTO[]): SourcingWorkbench => ({
  rfqId: 9001,
  lines: lines.map((item) => ({
    id: item.id,
    rfqId: 9001,
    description: item.productShortDescription ?? '',
    requestedQuantity: 10,
    availableQuantity: 10,
    reservedQuantity: 0,
    shortfallQuantity: 0,
    resolution: 'IN_STOCK' as const,
  })),
  solicitations: [],
  offers: [],
  awards: [],
  purchaseOrders: [],
});

const intelligence = (over: Partial<RfqCommercialIntelligence> = {}): RfqCommercialIntelligence => ({
  rfqId: 9001,
  rfqNumber: 'RFQ-9001',
  nexoraSerial: 'NX-9001',
  readinessScore: 62.75,
  commercialDecision: 'ACTIONABLE_WITH_BLOCKERS',
  slaRisk: 'DEADLINE_NOT_RECORDED',
  clarificationRequired: false,
  nextBestAction: {
    code: 'RECOVER_COVERAGE',
    label: 'Recover line coverage',
    explanation: 'Resolve 1 blocked line of the 1 line being quoted before preparing the customer quote.',
    confidence: 0.9,
    userOverrideAllowed: true,
    overrideAction: '/procurement/rfqs/9001/sourcing',
    evidence: [],
  },
  lines: [],
  digitalTwin: {
    calculatedOn: '2026-08-16T00:00:00Z',
    validity: 'Current',
    mode: 'SHADOW',
    policyVersion: 'digital-twin-v2.3',
    scenarios: [],
    customerTargetBridges: [],
    predictivePricing: [],
    backtest: { status: 'INSUFFICIENT', holdoutCount: 0, cohort: 'No decided cohort', limitation: 'none' },
    overrideAction: '',
  },
  ...over,
});

const wrapper = ({ children }: { children: ReactNode }) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
};

beforeEach(() => {
  vi.clearAllMocks();
  testAccess.denied.clear();
  window.localStorage.clear();
  getRfq.mockResolvedValue(rfq());
  getWorkbench.mockResolvedValue(workbench(rfq().rfqitems));
  getRfqIntelligence.mockResolvedValue(intelligence());
  getRfqLineResolutions.mockResolvedValue([]);
  resolveLineProduct.mockResolvedValue({ lineId: 1, productId: 501, replayed: false });
  createProduct.mockReset();
  getProductById.mockReset();
  getProducts.mockResolvedValue({
    items: [{
      id: 501,
      partNo: 'VALVE-A',
      productName: 'Control Valve',
      qtyOnHand: 0,
      reorderPoint: 0,
      isActive: true,
      createdBy: 'qa',
      createdOn: '2026-08-01T00:00:00Z',
      images: [],
      attachments: [],
    }],
    totalItems: 1,
    pageNumber: 1,
    pageSize: 20,
    totalPages: 1,
  });
});

describe('ViewRFQPage — cross-module Lead links', () => {
  it('hides every RFQ-to-Lead destination when Leads:view is denied', async () => {
    testAccess.denied.add('Leads:view');
    render(<ViewRFQPage />, { wrapper });

    await screen.findAllByText('RFQ-9001');
    expect(screen.queryByRole('button', { name: 'Open Canonical Lead' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Open Lead decision record' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Open exact source evidence' })).not.toBeInTheDocument();
  });

  it('routes the general Canonical Lead action only to the guarded Lead detail destination', async () => {
    render(<ViewRFQPage />, { wrapper });

    // The general information block sits inside the collapsed "Request details" fold.
    fireEvent.click(await screen.findByRole('button', { name: /Request details/ }));
    fireEvent.click(await screen.findByRole('button', { name: 'Open Canonical Lead' }));
    expect(testAccess.navigate).toHaveBeenCalledWith('/procurement/leads/view/55');
  });

  it('takes exact line evidence to the Lead workbench Evidence stage', async () => {
    const sourcing = workbench(rfq().rfqitems);
    sourcing.lines[0] = {
      ...sourcing.lines[0],
      availableQuantity: 0,
      shortfallQuantity: 10,
      resolution: 'SHORTAGE',
    };
    getWorkbench.mockResolvedValue(sourcing);
    render(<ViewRFQPage />, { wrapper });

    const evidenceButtons = await screen.findAllByRole('button', {
      name: 'Inspect persisted source and normalization evidence',
    });
    fireEvent.click(evidenceButtons[0]);
    fireEvent.click(await screen.findByRole('button', { name: 'Open exact source evidence' }));

    expect(testAccess.navigate).toHaveBeenCalledWith(
      '/procurement/leads/55/workbench?stage=evidence',
    );
  });
});

describe('ViewRFQPage — Sourcing Case authority', () => {
  beforeEach(() => {
    const sourcing = workbench(rfq().rfqitems);
    sourcing.lines[0] = {
      ...sourcing.lines[0],
      availableQuantity: 0,
      shortfallQuantity: 10,
      resolution: 'SHORTAGE',
    };
    getWorkbench.mockResolvedValue(sourcing);
  });

  it('hides the mutation when Supplier History view is denied even if RFQ edit is granted', async () => {
    testAccess.denied.add('Supplier History:view');
    render(<ViewRFQPage />, { wrapper });

    await screen.findByText('10 to source · Shortage');
    expect(screen.queryByRole('button', { name: 'Create / Open Sourcing Case' })).not.toBeInTheDocument();
  });

  it('offers the mutation only when both server-required grants are present', async () => {
    render(<ViewRFQPage />, { wrapper });

    expect(await screen.findByRole('button', { name: 'Create / Open Sourcing Case' })).toBeInTheDocument();
  });
});

describe('ViewRFQPage — governed RFQ product resolution', () => {
  it('does not advertise catalogue mutation without both required grants', async () => {
    testAccess.denied.add('Products:view');
    render(<ViewRFQPage />, { wrapper });

    await screen.findAllByText('RFQ-9001');
    expect(screen.queryByRole('button', { name: 'Add to catalogue' })).not.toBeInTheDocument();
  });

  it('records a human product choice and its evidence reason', async () => {
    getRfq.mockResolvedValue(rfq({ rfqitems: [line(1)] }));
    getWorkbench.mockResolvedValue(workbench([line(1)]));
    render(<ViewRFQPage />, { wrapper });

    fireEvent.click(await screen.findByRole('button', { name: 'Add to catalogue' }));
    expect(await screen.findByRole('dialog', { name: 'Match to our product' })).toBeInTheDocument();

    const productInput = await screen.findByRole('combobox', { name: 'Product in your catalogue' });
    fireEvent.change(productInput, { target: { value: 'VALVE' } });
    fireEvent.click(await screen.findByRole('option', { name: /VALVE-A/ }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Reason' }), {
      target: { value: 'Customer part number VALVE-A matches the approved tenant catalogue record.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Use this product' }));

    await waitFor(() => {
      expect(resolveLineProduct).toHaveBeenCalledWith(
        9001,
        1,
        501,
        'Customer part number VALVE-A matches the approved tenant catalogue record.',
      );
    });
  });

  it('always says what Save is waiting for', async () => {
    getRfq.mockResolvedValue(rfq({ rfqitems: [line(1)] }));
    getWorkbench.mockResolvedValue(workbench([line(1)]));
    render(<ViewRFQPage />, { wrapper });

    fireEvent.click(await screen.findByRole('button', { name: 'Add to catalogue' }));
    expect(await screen.findByText('Pick a product from the list first.')).toBeInTheDocument();

    const productInput = screen.getByRole('combobox', { name: 'Product in your catalogue' });
    fireEvent.change(productInput, { target: { value: 'VALVE' } });
    fireEvent.click(await screen.findByRole('option', { name: /VALVE-A/ }));
    expect(screen.getByText('Add a one-line reason first.')).toBeInTheDocument();

    fireEvent.change(screen.getByRole('textbox', { name: 'Reason' }), { target: { value: 'Same part.' } });
    expect(screen.getByText('Ready.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Use this product' })).toBeEnabled();
  });

  /**
   * The customer's part is usually one the tenant has never stocked, so the search returns
   * nothing for the normal case. That was a dead end: leave for Products, create the entry by
   * hand, come back. It is now the add step, and it binds the line in the same click.
   */
  it('offers to add a new part to the catalogue when nothing matches, and binds the line in one step', async () => {
    getRfq.mockResolvedValue(rfq({ rfqitems: [line(1, { manufacturerName: 'ABB', productShortDescription: 'VALVE,SOLN,75 MM PS' })] }));
    getWorkbench.mockResolvedValue(workbench([line(1)]));
    getProducts.mockResolvedValue({ items: [], totalItems: 0, pageNumber: 1, pageSize: 20, totalPages: 0 });
    createProduct.mockResolvedValue({
      id: 777, partNo: 'MPN-1', productName: 'VALVE,SOLN,75 MM PS', qtyOnHand: 0, reorderPoint: 0, isActive: true,
      createdBy: 'qa', createdOn: '2026-09-13T00:00:00Z', images: [], attachments: [],
    });
    resolveLineProduct.mockResolvedValue({ lineId: 1, productId: 777, replayed: false });
    render(<ViewRFQPage />, { wrapper });

    fireEvent.click(await screen.findByRole('button', { name: 'Add to catalogue' }));
    // One question, not a form: no search box, no reason to type.
    const dialog = await screen.findByRole('dialog', { name: 'Add MPN-1 to your catalogue?' });
    expect(within(dialog).queryByRole('combobox')).not.toBeInTheDocument();
    expect(within(dialog).queryByRole('textbox')).not.toBeInTheDocument();

    fireEvent.click(within(dialog).getByRole('button', { name: 'Add to catalogue' }));

    await waitFor(() => expect(createProduct).toHaveBeenCalledTimes(1));
    const form = createProduct.mock.calls[0][0] as FormData;
    expect(form.get('partNo')).toBe('MPN-1');
    expect(form.get('productName')).toBe('VALVE,SOLN,75 MM PS');
    expect(form.get('description')).toBe('Manufacturer: ABB. VALVE,SOLN,75 MM PS');
    expect(form.get('isCatalogItem')).toBe('true');
    expect(form.get('buid')).toBe('7');

    await waitFor(() => {
      expect(resolveLineProduct).toHaveBeenCalledWith(9001, 1, 777, expect.stringContaining('MPN-1'));
    });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  /**
   * The owner's defect (2026-09-14): after "Not now" the line still showed the gold "Resolve catalogue
   * product" button, which opened the same long form again, and the next-step banner kept asking.
   * "Not now" is a choice, so it sticks: the line stops asking, the banner moves on, and the line can
   * still be added later. It survives a reload.
   */
  it('remembers "Not now": the line stops asking, the banner moves on, and the part can still be added later', async () => {
    getRfq.mockResolvedValue(rfq({ rfqitems: [line(1)] }));
    const bench = workbench([line(1)]);
    bench.lines[0] = { ...bench.lines[0], availableQuantity: 0, shortfallQuantity: 10, resolution: 'UNKNOWN' as const };
    getWorkbench.mockResolvedValue(bench);
    getProducts.mockResolvedValue({ items: [], totalItems: 0, pageNumber: 1, pageSize: 20, totalPages: 0 });
    const view = render(<ViewRFQPage />, { wrapper });

    expect(await screen.findByText(/1 line is not in your catalogue yet/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Add to catalogue' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add MPN-1 to your catalogue?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Not now' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(createProduct).not.toHaveBeenCalled();
    expect(resolveLineProduct).not.toHaveBeenCalled();
    expect(screen.getByText('Not in catalogue · left for now')).toBeInTheDocument();
    expect(screen.getByText('10 requested · left out of the catalogue for now, price it by hand')).toBeInTheDocument();
    expect(screen.queryByText(/not in your catalogue yet/)).not.toBeInTheDocument();
    expect(screen.queryByText(/not been put to a supplier yet/)).not.toBeInTheDocument();
    // Quiet, not the screen's main button, and still there for later.
    const addLater = screen.getByRole('button', { name: 'Add to catalogue' });
    expect(addLater).toHaveClass('MuiButton-outlined');

    view.unmount();
    render(<ViewRFQPage />, { wrapper });
    expect(await screen.findByText('Not in catalogue · left for now')).toBeInTheDocument();
  });

  it('lets the user search instead when the part is in the catalogue under another number', async () => {
    getRfq.mockResolvedValue(rfq({ rfqitems: [line(1)] }));
    getWorkbench.mockResolvedValue(workbench([line(1)]));
    const valve = {
      id: 501, partNo: 'VALVE-A', productName: 'Control Valve', qtyOnHand: 0, reorderPoint: 0, isActive: true,
      createdBy: 'qa', createdOn: '2026-08-01T00:00:00Z', images: [], attachments: [],
    };
    getProducts.mockImplementation(async ({ search }: { search?: string }) => (search === 'MPN-1'
      ? { items: [], totalItems: 0, pageNumber: 1, pageSize: 20, totalPages: 0 }
      : { items: [valve], totalItems: 1, pageNumber: 1, pageSize: 20, totalPages: 1 }));
    render(<ViewRFQPage />, { wrapper });

    fireEvent.click(await screen.findByRole('button', { name: 'Add to catalogue' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add MPN-1 to your catalogue?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Already in the catalogue under another number? Search for it' }));

    const productInput = await screen.findByRole('combobox', { name: 'Product in your catalogue' });
    fireEvent.change(productInput, { target: { value: 'VALVE' } });
    fireEvent.click(await screen.findByRole('option', { name: /VALVE-A/ }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Reason' }), { target: { value: 'Customer code for our VALVE-A.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Use this product' }));
    await waitFor(() => expect(resolveLineProduct).toHaveBeenCalledWith(9001, 1, 501, 'Customer code for our VALVE-A.'));
    expect(createProduct).not.toHaveBeenCalled();
  });

  it('does not offer the add step without Products:create, but still says the part is missing', async () => {
    testAccess.denied.add('Products:create');
    getRfq.mockResolvedValue(rfq({ rfqitems: [line(1)] }));
    getWorkbench.mockResolvedValue(workbench([line(1)]));
    getProducts.mockResolvedValue({ items: [], totalItems: 0, pageNumber: 1, pageSize: 20, totalPages: 0 });
    render(<ViewRFQPage />, { wrapper });

    fireEvent.click(await screen.findByRole('button', { name: 'Add to catalogue' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add MPN-1 to your catalogue?' });
    expect(within(dialog).getByText('It is not in your catalogue yet. Ask someone who can add products to add it.')).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Add to catalogue' })).not.toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Not now' })).toBeInTheDocument();
    expect(createProduct).not.toHaveBeenCalled();
  });

  it('keeps the product when it was created but the line could not be bound, so Save is the retry', async () => {
    getRfq.mockResolvedValue(rfq({ rfqitems: [line(1)] }));
    getWorkbench.mockResolvedValue(workbench([line(1)]));
    getProducts.mockResolvedValue({ items: [], totalItems: 0, pageNumber: 1, pageSize: 20, totalPages: 0 });
    createProduct.mockResolvedValue({
      id: 778, partNo: 'MPN-1', productName: 'Line 1', qtyOnHand: 0, reorderPoint: 0, isActive: true,
      createdBy: 'qa', createdOn: '2026-09-13T00:00:00Z', images: [], attachments: [],
    });
    resolveLineProduct.mockRejectedValueOnce(new Error('network'));
    render(<ViewRFQPage />, { wrapper });

    fireEvent.click(await screen.findByRole('button', { name: 'Add to catalogue' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add MPN-1 to your catalogue?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add to catalogue' }));

    await waitFor(() => expect(resolveLineProduct).toHaveBeenCalledTimes(1));
    expect(await screen.findByText('Ready.')).toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: 'Match to our product' })).toBeInTheDocument();
    expect(createProduct).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole('button', { name: 'Use this product' }));
    await waitFor(() => expect(resolveLineProduct).toHaveBeenLastCalledWith(9001, 1, 778, expect.stringContaining('MPN-1')));
    expect(createProduct).toHaveBeenCalledTimes(1);
  });

  it('preselects the current product when a bound line is being changed', async () => {
    getRfq.mockResolvedValue(rfq({ rfqitems: [line(1, { productId: 501, productName: 'Control Valve' })] }));
    getWorkbench.mockResolvedValue(workbench([line(1)]));
    getProductById.mockResolvedValue({
      id: 501, partNo: 'VALVE-A', productName: 'Control Valve', qtyOnHand: 0, reorderPoint: 0, isActive: true,
      createdBy: 'qa', createdOn: '2026-08-01T00:00:00Z', images: [], attachments: [],
    });
    render(<ViewRFQPage />, { wrapper });

    fireEvent.click(await screen.findByRole('button', { name: 'Change product' }));
    expect(await screen.findByText('Add a one-line reason first.')).toBeInTheDocument();
    fireEvent.change(screen.getByRole('textbox', { name: 'Reason' }), { target: { value: 'Confirmed against the drawing.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Use this product' }));
    await waitFor(() => expect(resolveLineProduct).toHaveBeenCalledWith(9001, 1, 501, 'Confirmed against the drawing.'));
  });
});

describe('ViewRFQPage — a line without a product says so, and is not offered a step the server refuses', () => {
  it('reads "Not in catalogue" and "Matched to catalogue" from the line itself, not from the ledger', async () => {
    getRfq.mockResolvedValue(rfq({ rfqitems: [line(1), line(2, { productId: 501, productName: 'Control Valve' })] }));
    getWorkbench.mockResolvedValue(workbench([line(1), line(2)]));
    render(<ViewRFQPage />, { wrapper });

    expect(await screen.findByText('Not in catalogue')).toBeInTheDocument();
    expect(screen.getByText('Matched to catalogue')).toBeInTheDocument();
    expect(screen.queryByText('Resolution not recorded')).not.toBeInTheDocument();
  });

  it('offers the catalogue step instead of a Sourcing Case on an UNKNOWN line, and does not print a stock answer', async () => {
    getRfq.mockResolvedValue(rfq({ rfqitems: [line(1)] }));
    const bench = workbench([line(1)]);
    bench.lines[0] = { ...bench.lines[0], availableQuantity: 0, shortfallQuantity: 10, resolution: 'UNKNOWN' as const };
    getWorkbench.mockResolvedValue(bench);
    render(<ViewRFQPage />, { wrapper });

    expect(await screen.findByText('10 requested · not in your catalogue yet')).toBeInTheDocument();
    expect(screen.getByText('Not checked')).toBeInTheDocument();
    expect(screen.queryByText(/Available 0 · Short 10/)).not.toBeInTheDocument();
    // The case button would only fail on a line with no product; the line offers "Ask suppliers" instead.
    expect(screen.queryByRole('button', { name: 'Create / Open Sourcing Case' })).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Ask suppliers' })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'Add to catalogue' })).toHaveLength(1);
  });

  const unknownLine = () => {
    getRfq.mockResolvedValue(rfq({ rfqitems: [line(1, { manufacturerName: 'TELEDYNE', productShortDescription: 'VALVE,SOLN,1/4 IN PS' })] }));
    const bench = workbench([line(1)]);
    bench.lines[0] = { ...bench.lines[0], availableQuantity: 0, shortfallQuantity: 10, resolution: 'UNKNOWN' as const };
    getWorkbench.mockResolvedValue(bench);
    createOrOpenSourcingCase.mockResolvedValue({ id: 44 });
  };

  it('"Ask suppliers" reuses the catalogue product with the same part number, links the line and opens the case', async () => {
    unknownLine();
    getProducts.mockResolvedValue({
      items: [{ id: 501, partNo: 'mpn 1', productName: 'Valve', qtyOnHand: 0, reorderPoint: 0, isActive: true, createdBy: 'qa', createdOn: '2026-08-01T00:00:00Z', images: [], attachments: [] }],
      totalItems: 1, pageNumber: 1, pageSize: 20, totalPages: 1,
    });
    render(<ViewRFQPage />, { wrapper });

    fireEvent.click(await screen.findByRole('button', { name: 'Ask suppliers' }));

    await waitFor(() => expect(createOrOpenSourcingCase).toHaveBeenCalledWith(9001, 1, 10));
    expect(createProduct).not.toHaveBeenCalled();
    expect(resolveLineProduct).toHaveBeenCalledWith(9001, 1, 501, expect.stringContaining('same part number'));
    await waitFor(() => expect(testAccess.navigate).toHaveBeenCalledWith('/procurement/sourcing-cases/44'));
  });

  it('"Ask suppliers" adds the part when the catalogue has no such part number, then links and opens the case', async () => {
    unknownLine();
    getProducts.mockResolvedValue({ items: [], totalItems: 0, pageNumber: 1, pageSize: 20, totalPages: 0 });
    createProduct.mockResolvedValue({
      id: 777, partNo: 'MPN-1', productName: 'VALVE,SOLN,1/4 IN PS', qtyOnHand: 0, reorderPoint: 0, isActive: true,
      createdBy: 'qa', createdOn: '2026-09-14T00:00:00Z', images: [], attachments: [],
    });
    resolveLineProduct.mockResolvedValue({ lineId: 1, productId: 777, replayed: false });
    render(<ViewRFQPage />, { wrapper });

    fireEvent.click(await screen.findByRole('button', { name: 'Ask suppliers' }));

    await waitFor(() => expect(createOrOpenSourcingCase).toHaveBeenCalledWith(9001, 1, 10));
    const form = createProduct.mock.calls[0][0] as FormData;
    expect(form.get('partNo')).toBe('MPN-1');
    expect(form.get('description')).toBe('Manufacturer: TELEDYNE. VALVE,SOLN,1/4 IN PS');
    expect(resolveLineProduct).toHaveBeenCalledWith(9001, 1, 777, expect.stringContaining('Added MPN-1'));
    await waitFor(() => expect(testAccess.navigate).toHaveBeenCalledWith('/procurement/sourcing-cases/44'));
  });

  it('does not open a case when the line could not be linked, and never offers "Ask suppliers" without both rights', async () => {
    unknownLine();
    getProducts.mockResolvedValue({ items: [], totalItems: 0, pageNumber: 1, pageSize: 20, totalPages: 0 });
    createProduct.mockResolvedValue({ id: 777, partNo: 'MPN-1', productName: 'x', qtyOnHand: 0, reorderPoint: 0, isActive: true, createdBy: 'qa', createdOn: '2026-09-14T00:00:00Z', images: [], attachments: [] });
    resolveLineProduct.mockRejectedValue(new Error('conflict'));
    const { unmount } = render(<ViewRFQPage />, { wrapper });

    fireEvent.click(await screen.findByRole('button', { name: 'Ask suppliers' }));
    await waitFor(() => expect(resolveLineProduct).toHaveBeenCalled());
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(createOrOpenSourcingCase).not.toHaveBeenCalled();
    expect(testAccess.navigate).not.toHaveBeenCalledWith('/procurement/sourcing-cases/44');
    unmount();

    testAccess.denied.add('Products:create');
    render(<ViewRFQPage />, { wrapper });
    expect(await screen.findByText('10 requested · needs a catalogue product before sourcing')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ask suppliers' })).not.toBeInTheDocument();
  });
});

/**
 * The line table renders `visibleItems`, but its "nothing here" row was keyed on the RFQ's own
 * line collection. So the message appeared only for an RFQ with no lines at all — the one case
 * where "match this filter" means nothing — and a tile filter that genuinely matched nothing
 * left the operator staring at an empty table with no explanation and no way back.
 */
describe('ViewRFQPage — a filter that matches nothing says so', () => {
  it('explains an empty result and offers a way back to all lines', async () => {
    render(<ViewRFQPage />, { wrapper });

    // Nothing is ready for quote: intelligence reported no lines at all.
    const readyTile = await screen.findByText(/^Ready for quote/);
    readyTile.click();

    expect(await screen.findByText(/No line matches "Ready for quote/)).toBeInTheDocument();
    expect(screen.getByText(/3 lines on this RFQ/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show all lines' })).toBeInTheDocument();
  });

  it('says the RFQ is empty rather than blaming a filter, when it truly has no lines', async () => {
    getRfq.mockResolvedValue(rfq({ rfqitems: [] }));
    getWorkbench.mockResolvedValue(workbench([]));
    render(<ViewRFQPage />, { wrapper });

    expect(await screen.findByText('This RFQ has no lines yet.')).toBeInTheDocument();
  });
});

/**
 * The buyer's own line number is the identifier they will quote back at you. Rendering the
 * position in a filtered array meant the number on screen changed when a tile was clicked.
 */
describe('ViewRFQPage — line identity', () => {
  it("shows the buyer's line number, not a position in the filtered array", async () => {
    render(<ViewRFQPage />, { wrapper });

    expect(await screen.findByText('0010')).toBeInTheDocument();
    expect(screen.getByText('0020')).toBeInTheDocument();
    expect(screen.getByText('0030')).toBeInTheDocument();
  });

  it('falls back to the position only when the document carried no line number', async () => {
    getRfq.mockResolvedValue(rfq({ rfqitems: [line(1, { lineItemNo: undefined })] }));
    render(<ViewRFQPage />, { wrapper });

    const rows = await screen.findAllByRole('row');
    expect(within(rows[rows.length - 1]).getByText('1')).toBeInTheDocument();
  });
});

describe('ViewRFQPage — immutable promotion lineage', () => {
  it('shows the receipt, Lead revision, participation version, and source-line trace supplied by the API', async () => {
    getRfq.mockResolvedValue(rfq({
      promotionId: 901,
      sourceLeadRevisionId: 5502,
      sourceLeadRevisionNumber: 2,
      participationDecisionId: 333,
      participationVersion: 4,
      promotedAtUtc: '2026-08-24T12:00:00Z',
      promotedBy: 'Bid Manager',
      rfqitems: [line(1, { sourceLeadItemRevisionId: 55101 })],
    }));
    render(<ViewRFQPage />, { wrapper });

    expect(await screen.findByText('Governed promotion receipt')).toBeInTheDocument();
    expect(screen.getByText('Revision 2')).toBeInTheDocument();
    expect(screen.getByText('Version 4')).toBeInTheDocument();
    expect(screen.getByText('#901')).toBeInTheDocument();
    expect(screen.getByText('Immutable source record #55101')).toBeInTheDocument();
    expect(screen.queryByText('Revision line #55101')).not.toBeInTheDocument();
    expect(screen.queryByText('Quote?')).not.toBeInTheDocument();
  });

  it('states that lineage is unavailable instead of inferring it for a legacy RFQ', async () => {
    render(<ViewRFQPage />, { wrapper });

    expect(await screen.findByText('Promotion receipt unavailable')).toBeInTheDocument();
    expect(screen.getByText(/immutable source lineage cannot be claimed/i)).toBeInTheDocument();
    expect(screen.getAllByText('Trace unavailable')).toHaveLength(3);
    expect(screen.getByText('Ready for quote of 3 lines')).toBeInTheDocument();
  });
});

describe('ViewRFQPage — immutable customer request terms', () => {
  it('shows the customer header terms copied into the formal RFQ', async () => {
    getRfq.mockResolvedValue(rfq({
      customerRfqReference: 'CUSTOMER-RFQ-77',
      requiredDeliveryDate: '2026-09-15T00:00:00Z',
      deliveryLocation: 'Plant 4 · Receiving Bay B',
      agreementReference: 'FRAME-2026-09',
      bidClosingDateHijri: '1448-04-03',
      inquiryType: 'product',
    }));
    render(<ViewRFQPage />, { wrapper });

    fireEvent.click(await screen.findByRole('button', { name: /Request details/ }));
    expect(await screen.findByRole('heading', { name: 'Customer request terms' })).toBeInTheDocument();
    expect(screen.getByText('CUSTOMER-RFQ-77')).toBeInTheDocument();
    expect(screen.getByText('Plant 4 · Receiving Bay B')).toBeInTheDocument();
    expect(screen.getByText('FRAME-2026-09')).toBeInTheDocument();
    expect(screen.getByText('1448-04-03')).toBeInTheDocument();
    expect(screen.getByText('product')).toBeInTheDocument();
  });

  it('renders only a bounded, escaped line-field summary and retains the remaining count', async () => {
    const extraFields = JSON.stringify({
      plant: '<script>unsafe()</script>',
      incoterm: 'DAP',
      project_code: 'P-42',
      costCentre: 'CC-9',
      drawing: 'A'.repeat(180),
      nested: { confidential: 'not rendered raw' },
      seventh: 'retained',
    });
    getRfq.mockResolvedValue(rfq({ rfqitems: [line(1, { extraFields })] }));
    render(<ViewRFQPage />, { wrapper });

    const summary = await screen.findByText('Customer fields (7)');
    fireEvent.click(summary);
    expect(screen.getByText('<script>unsafe()</script>')).toBeInTheDocument();
    expect(screen.queryByText('unsafe()', { selector: 'script' })).not.toBeInTheDocument();
    expect(screen.getByText(/Structured value retained in the source record/)).toBeInTheDocument();
    expect(screen.getByText('1 additional customer field retained in the source record.')).toBeInTheDocument();
    expect(screen.queryByText('retained', { exact: true })).not.toBeInTheDocument();
  });

  it('fails closed without rendering malformed JSON', () => {
    expect(presentRfqExtraFields('{not-json')).toEqual({
      fields: [],
      hiddenCount: 0,
      invalid: true,
    });
  });
});

/**
 * The screen carried three readiness statements. Two were constants: `rfq.readiness` derived
 * from an ItemCount the detail endpoint never populated, and a literal warning chip wired to
 * nothing at all. The evidence-backed score is the one that means something.
 */
describe('ViewRFQPage — one readiness statement, not three', () => {
  it('does not render a constant readiness field or a constant review chip', async () => {
    getRfq.mockResolvedValue(rfq({ readiness: 'Review Required' }));
    render(<ViewRFQPage />, { wrapper });

    await screen.findAllByText('RFQ-9001');
    expect(screen.queryByText('Review Required')).not.toBeInTheDocument();
    expect(screen.queryByText('Commercial Review Required')).not.toBeInTheDocument();
    // The evidence-backed score stays, rounded — the server sends 62.75 and this is a heuristic.
    expect(screen.getByText('63%')).toBeInTheDocument();
  });
});

/**
 * A sentinel date must never be coloured and presented as an overdue customer deadline.
 */
describe('ViewRFQPage — dates', () => {
  it('renders DateTime.MinValue as "not set", not as 01 Jan 1', async () => {
    getRfq.mockResolvedValue(rfq({
      bidClosingDate: '0001-01-01T00:00:00',
      recDate: '0001-01-01T00:00:00',
    }));
    render(<ViewRFQPage />, { wrapper });

    await screen.findAllByText('RFQ-9001');
    expect(screen.queryByText(/01 Jan 1$/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Jan 0001/)).not.toBeInTheDocument();
  });
});

/**
 * A disabled primary action with no stated reason is what one transient intelligence failure
 * used to leave behind.
 */
describe('ViewRFQPage — the primary action states why it is unavailable', () => {
  it('gives the quote-draft button a reason drawn from the actual blocker', async () => {
    // Fixture corrected: this case must be one the SERVER actually refuses. The default fixture
    // is ACTIONABLE_WITH_BLOCKERS, and QuoteService.PrepareQuoteDraftAsync refuses only
    // NO_QUOTE_REVIEW — it says so in a comment, because demanding VIABLE_READY "made the draft
    // unreachable for any line needing sourcing — the normal case." The client had kept
    // enforcing the abandoned rule, so this assertion was pinning the defect rather than the
    // intent. The intent — a disabled action always states why — is preserved below and is right.
    getRfqIntelligence.mockResolvedValue(intelligence({ commercialDecision: 'NO_QUOTE_REVIEW' }));
    render(<ViewRFQPage />, { wrapper });

    const button = await screen.findByRole('button', { name: /Prepare Quote Draft/i });
    expect(button).toBeDisabled();
    expect(button.closest('span')).toHaveAttribute(
      'aria-label',
      'Resolve 1 blocked line of the 1 line being quoted before preparing the customer quote.',
    );
    expect(button.closest('span')).toHaveAttribute('tabindex', '0');
  });

  it('lets a rep start a quote whose lines still need sourcing, and says so honestly', async () => {
    // The everyday case for a distributor: lines not in stock. The server allows the draft; the
    // client used to grey the button out with an explanation the rep could not act on.
    getRfqIntelligence.mockResolvedValue(intelligence({ commercialDecision: 'ACTIONABLE_WITH_BLOCKERS' }));
    render(<ViewRFQPage />, { wrapper });

    const button = await screen.findByRole('button', { name: /Prepare Quote Draft/i });
    expect(button).toBeEnabled();
    // And the reason must not claim every line is covered when it plainly is not.
    const label = button.closest('span')?.getAttribute('aria-label') ?? '';
    expect(label).toMatch(/You can start the quote now/i);
    expect(label).not.toMatch(/evidence-backed fulfilment route/i);
  });
});
