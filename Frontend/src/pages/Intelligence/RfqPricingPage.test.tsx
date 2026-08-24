import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import RfqPricingPage from './RfqPricingPage';
import type { PricePreview, PricePreviewLine } from '../../api/services/intelligenceService';

/**
 * These tests exist to make the page's HONESTY unrefactorable.
 *
 * The Smart Pricing screen is deliberately shadow-only: `POST /api/intelligence/rfqs/{id}/apply-pricing`
 * returns 409 unconditionally and `PricingEngine.ApplyPricingAsync` throws, because authoritative
 * pricing belongs to the governed Supplier award -> Customer Quote bridge. That makes the ABSENCE of
 * a save control a requirement, not an oversight — `commercial-journey-v2.spec.ts` already asserts
 * `getByRole('button', { name: 'Apply pricing' })` has count 0. Nothing at the component level
 * protected it until now, so a refactor could have reintroduced the false affordance and stayed green.
 *
 * axiosInstance is mocked rather than intelligenceService, deliberately: the real service module runs,
 * so "this page never writes" is proved against the actual transport instead of against a stub that
 * could not have written anyway.
 */

const get = vi.fn();
const post = vi.fn();
const put = vi.fn();
const patch = vi.fn();
const del = vi.fn();
const hasPermission = vi.fn();

vi.mock('../../api/axiosInstance', () => ({
  default: {
    get: (url: string, config?: unknown) => get(url, config),
    post: (url: string, body?: unknown, config?: unknown) => post(url, body, config),
    put: (url: string, body?: unknown, config?: unknown) => put(url, body, config),
    patch: (url: string, body?: unknown, config?: unknown) => patch(url, body, config),
    delete: (url: string, config?: unknown) => del(url, config),
  },
}));

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: (module: string, action?: string) => hasPermission(module, action) }),
}));

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => vi.fn(), useParams: () => ({ id: '9001' }) };
});

const SOURCING_PATH = '/procurement/rfqs/9001/sourcing';

/**
 * Matches what the server actually emits, per nexora-verify #2:
 *  - `mode` is the literal "SHADOW" and `applyAllowed` is false — both pinned by
 *    Release02CommercialLearningTests.
 *  - `applyBlocker` is PricePreview's default in PricingModels.cs, which nothing in the backend ever
 *    reassigns (`grep "ApplyBlocker\s*="` finds no assignment), so this is the only string a user sees.
 */
const line = (over: Partial<PricePreviewLine> = {}): PricePreviewLine => ({
  rfqItemId: 7001,
  description: 'Ball valve 2in 150#',
  quantity: 10,
  currency: 'SAR',
  recommendedUnitPrice: 18,
  floorUnitPrice: 13.5,
  floorCurrency: 'SAR',
  floorBasis: 'Cost floor 13.5 SAR — the awarded supplier\'s landed unit cost from sourcing decision 42.',
  marginPct: 25,
  confidence: 0.8,
  rationale: 'Blended from one accepted quote.',
  signals: [],
  needsAttention: false,
  ...over,
});

const preview = (lines: PricePreviewLine[] = [line()]): PricePreview => ({
  rfqId: 9001,
  currency: 'SAR',
  mode: 'SHADOW',
  applyAllowed: false,
  applyBlocker: 'Create or revise the Customer Quote through the governed Supplier award pricing bridge.',
  lines,
  totals: {
    recommendedTotal: 180,
    byCurrency: [{ currency: 'SAR', recommendedTotal: 180, lineCount: lines.length }],
    pricedLineCount: lines.length,
    unpricedLineCount: 0,
  },
  overallConfidence: 0.8,
});

const wrapper = ({ children }: { children: ReactNode }) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <QueryClientProvider client={client}>
      <MemoryRouter>{children}</MemoryRouter>
    </QueryClientProvider>
  );
};

const renderPage = async () => {
  render(<RfqPricingPage />, { wrapper });
  return screen.findByRole('heading', { name: 'Shadow pricing workspace' });
};

beforeEach(() => {
  vi.clearAllMocks();
  hasPermission.mockReturnValue(true);
  get.mockResolvedValue({ data: preview() });
});

describe('RfqPricingPage — the disclosures that keep it honest', () => {
  it('names itself a shadow workspace and says the edits are thrown away', async () => {
    await renderPage();

    expect(screen.getByText(/without changing the RFQ or Customer Quote/i)).toBeInTheDocument();
    expect(
      screen.getByText(/What-if edits are temporary and are discarded when you leave this page/i)
    ).toBeInTheDocument();
  });

  it('offers no save, apply or confirm control — before or after a price is typed', async () => {
    await renderPage();

    const savingControl = /save|apply|confirm|submit/i;
    expect(screen.queryByRole('button', { name: savingControl })).toBeNull();
    expect(screen.queryByRole('link', { name: savingControl })).toBeNull();
    // The exact control commercial-journey-v2.spec.ts forbids.
    expect(screen.queryByRole('button', { name: 'Apply pricing' })).toBeNull();

    fireEvent.change(screen.getByLabelText(/What-if unit price/i), { target: { value: '20' } });
    expect(screen.getByLabelText(/What-if unit price/i)).toHaveValue('20');

    // Typing a price must not summon a control that promises to keep it.
    expect(screen.queryByRole('button', { name: savingControl })).toBeNull();
    expect(screen.queryByRole('link', { name: savingControl })).toBeNull();
  });

  it('never writes: typing a price sends no request of any kind', async () => {
    await renderPage();

    fireEvent.change(screen.getByLabelText(/What-if unit price/i), { target: { value: '20' } });
    fireEvent.blur(screen.getByLabelText(/What-if unit price/i));

    await waitFor(() => expect(screen.getByLabelText(/What-if unit price/i)).toHaveValue('20'));
    expect(post).not.toHaveBeenCalled();
    expect(put).not.toHaveBeenCalled();
    expect(patch).not.toHaveBeenCalled();
    expect(del).not.toHaveBeenCalled();
    // The single read that renders the page, and nothing else.
    expect(get).toHaveBeenCalledTimes(1);
    expect(get).toHaveBeenCalledWith('/api/intelligence/rfqs/9001/price-preview', undefined);
  });
});

describe('RfqPricingPage — where pricing is actually done', () => {
  it('sends an awarded RFQ to its Sourcing workspace and names the control to click there', async () => {
    await renderPage();

    const link = screen.getByRole('link', { name: /Go to Sourcing to set prices/i });
    expect(link).toHaveAttribute('href', SOURCING_PATH);
    expect(screen.getByText(/Price customer quote/i)).toBeInTheDocument();
    expect(screen.getByText(/margin over the supplier's approved cost/i)).toBeInTheDocument();
  });

  it('says the award has to happen first when no line has an approved cost', async () => {
    get.mockResolvedValue({
      data: preview([
        line({ rfqItemId: 7001, floorUnitPrice: null, floorCurrency: null, floorBasis: null, marginPct: null }),
        line({ rfqItemId: 7002, floorUnitPrice: null, floorCurrency: null, floorBasis: null, marginPct: null }),
      ]),
    });
    await renderPage();

    expect(
      screen.getByText(/No supplier has been awarded on this RFQ yet, so there is no approved cost to price against/i)
    ).toBeInTheDocument();
    // Still a live destination — Sourcing is where the award is made — but it promises the award, not a price.
    const link = screen.getByRole('link', { name: /Go to Sourcing to award a supplier/i });
    expect(link).toHaveAttribute('href', SOURCING_PATH);
    expect(screen.queryByRole('link', { name: /Go to Sourcing to set prices/i })).toBeNull();
  });

  it('links only when ONE line has an approved cost, not when every line must', async () => {
    get.mockResolvedValue({
      data: preview([
        line({ rfqItemId: 7001, floorUnitPrice: null, floorCurrency: null, floorBasis: null, marginPct: null }),
        line({ rfqItemId: 7002 }),
      ]),
    });
    await renderPage();

    expect(screen.getByRole('link', { name: /Go to Sourcing to set prices/i })).toHaveAttribute('href', SOURCING_PATH);
  });
});

describe('RfqPricingPage — degrading honestly instead of dangling a dead link', () => {
  /**
   * SourcingWorkbenchPage gates its "Price customer quote" button on Supplier History:edit AND
   * Quotations:edit — the same pair SupplierQuoteInboxController.ApplyCustomerPricing requires.
   * Either one missing means the user would arrive and find no control, so no link is offered.
   */
  it.each([
    ['Quotations', 'edit'],
    ['Supplier History', 'edit'],
  ])('offers no link when the user lacks %s:%s, and says which permissions are needed', async (module, action) => {
    hasPermission.mockImplementation((m: string, a?: string) => !(m === module && a === action));
    await renderPage();

    expect(screen.queryByRole('link', { name: /Go to Sourcing/i })).toBeNull();
    expect(screen.getByText(/needs "Can Edit" on both Quotations and Supplier History/i)).toBeInTheDocument();
    expect(screen.getByText(/Ask an administrator to add them/i)).toBeInTheDocument();
  });

  it('still refuses to invent a save control for a user who cannot price', async () => {
    hasPermission.mockReturnValue(false);
    await renderPage();

    expect(screen.queryByRole('button', { name: /save|apply|confirm|submit/i })).toBeNull();
    expect(
      screen.getByText(/What-if edits are temporary and are discarded when you leave this page/i)
    ).toBeInTheDocument();
  });
});
