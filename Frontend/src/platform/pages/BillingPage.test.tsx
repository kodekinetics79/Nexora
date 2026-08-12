import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { platformApi } from '../api/client';
import type { BillingMeterCatalogEntry } from '../types';
import { ThemeContextProvider } from '../../context/ThemeContext';
import BillingPage from './BillingPage';

vi.mock('../auth/usePlatformPermissions', () => ({
  usePlatformPermissions: () => ({
    role: 'Owner', isOwner: true, canAdministerTenants: true,
    canAdministerBilling: true, canImpersonate: true, roleUnknown: false,
  }),
}));

/**
 * The rate-card meter field used to be free text over a CLOSED server catalogue. Only four of the
 * sixteen meters are BILLING_CERTIFIED, and `ValidateRateCardShape` refuses the rest — so the only
 * way to learn any of that was to invent a key, save, and read
 *
 *   Meter 'Test-001' is not BILLING_CERTIFIED and cannot be placed on a rate card.
 *
 * which reads like there is a certification workflow to go and complete. There isn't; the status is
 * compiled in. Worse, `GET /api/platform/billing/meter-catalog`, the `BillingMeterCatalogEntry`
 * type, the `listBillingMeterCatalog` client method and the `billingMeterCatalog` query key all
 * already existed. Nothing called them.
 */

// Mirrors the four certifications the server actually ships, including the detail that bit:
// the rate card matches on billingMeterKey, NOT on the catalogue's event type. `ai.tokens` is
// stored as `ai.tokens.external`, and typing the event type gets you the same 400.
const catalog: BillingMeterCatalogEntry[] = [
  { eventType: 'documents', billingMeterKey: 'documents', unit: 'document', certification: 'BillingCertified' },
  { eventType: 'ai.tokens', billingMeterKey: 'ai.tokens.external', unit: 'token', certification: 'BillingCertified' },
  { eventType: 'base.subscription', billingMeterKey: 'base.subscription', unit: 'subscription', certification: 'BillingCertified' },
  { eventType: 'users', billingMeterKey: 'seats', unit: 'user', certification: 'BillingCertified' },
  { eventType: 'pages.processed', billingMeterKey: 'pages.processed', unit: 'page', certification: 'Blocked' },
  { eventType: 'rfqs', billingMeterKey: 'rfqs', unit: 'rfq', certification: 'NotImplemented' },
];

// StatTile reads useAppTheme, so the revenue tiles throw without the provider — same wrapper
// OverviewPage.test.tsx and PipelinePage.test.tsx use.
const renderPage = () => render(
  <MemoryRouter>
    <ThemeContextProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <SnackbarProvider><BillingPage /></SnackbarProvider>
      </QueryClientProvider>
    </ThemeContextProvider>
  </MemoryRouter>,
);

/**
 * Generous because these are liveness guards, not assertions — the assertions are in the tests
 * below, and a dialog that never opens fails just as surely at ten seconds as at one. Short
 * discovery timeouts do not measure the product, they measure how busy the machine is, and report
 * the answer as a defect; that is what turned `main` red this morning in
 * `ExtractionWorkerLeaseTests` (see `Backend/ERP_RFQ_Automation.Tests/TestWaits.cs`).
 */
const FIND_TIMEOUT = { timeout: 10_000 };

const openCreateDialog = async () => {
  renderPage();
  fireEvent.click(await screen.findByRole('button', { name: /New rate card/i }, FIND_TIMEOUT));
  return within(await screen.findByRole('dialog', undefined, FIND_TIMEOUT));
};

beforeEach(() => {
  vi.restoreAllMocks();
  vi.spyOn(platformApi, 'listTenants').mockResolvedValue([]);
  // A complete report. The page formats every count into a tile, so a partial stub renders
  // undefined through fmtNumber and the whole branch — Rate Cards included — never appears.
  vi.spyOn(platformApi, 'getRevenueRisk').mockResolvedValue({
    generatedAtUtc: '2026-08-12T00:00:00Z',
    tenantCount: 0,
    atRiskCount: 0,
    expiredTrialCount: 0,
    billableTenantsChargedNothingCount: 0,
    commercialConfigurationRequiredCount: 0,
    tenants: [],
  });
  vi.spyOn(platformApi, 'listStatements').mockResolvedValue([]);
  vi.spyOn(platformApi, 'listRateCards').mockResolvedValue([]);
  vi.spyOn(platformApi, 'listBillingMeterCatalog').mockResolvedValue(catalog);
});

describe('BillingPage rate-card meter selection', () => {
  it('offers the certified meters and refuses to offer the ones the server would reject', async () => {
    const dialog = await openCreateDialog();
    fireEvent.mouseDown(dialog.getByLabelText(/Meter key/i));
    const list = within(await screen.findByRole('listbox', undefined, FIND_TIMEOUT));

    for (const key of ['documents', 'ai.tokens.external', 'base.subscription', 'seats']) {
      expect(list.getByRole('option', { name: new RegExp(key.replace(/\./g, '\\.')) })).not.toHaveAttribute('aria-disabled', 'true');
    }

    // Present but unselectable. Hiding them turns "why can't I bill pages?" into a search of the
    // codebase; showing them disabled answers it in the dropdown.
    expect(list.getByRole('option', { name: /pages\.processed/ })).toHaveAttribute('aria-disabled', 'true');
    expect(list.getByRole('option', { name: /rfqs/ })).toHaveAttribute('aria-disabled', 'true');
  });

  it('fills the unit from the catalogue when a meter is chosen', async () => {
    const dialog = await openCreateDialog();
    fireEvent.mouseDown(dialog.getByLabelText(/Meter key/i));
    fireEvent.click(within(await screen.findByRole('listbox', undefined, FIND_TIMEOUT)).getByRole('option', { name: /ai\.tokens\.external/ }));

    // Hand-typing the unit is how a line gets priced per "tokens" against a meter that emits
    // "token" — the catalogue is where that answer lives, so it is taken from there.
    // `Unit`, not `Unit price` — and the field is required, so MUI renders the label as "Unit *".
    await waitFor(() => expect(dialog.getByRole('textbox', { name: /^Unit\s*\*?$/i })).toHaveValue('token'));
  });

  it('keeps a custom escape hatch, last, and says what the server will do with it', async () => {
    const dialog = await openCreateDialog();
    fireEvent.mouseDown(dialog.getByLabelText(/Meter key/i));
    const options = within(await screen.findByRole('listbox', undefined, FIND_TIMEOUT)).getAllByRole('option');
    expect(options[options.length - 1]).toHaveTextContent(/Custom/i);

    fireEvent.click(options[options.length - 1]);

    // Now free text — the catalogue is compiled into the console, so a meter certified on the
    // server before this build ships must still be reachable.
    const field = dialog.getByLabelText(/Meter key/i);
    fireEvent.change(field, { target: { value: 'Test-001' } });
    expect(field).toHaveValue('Test-001');
    expect(dialog.getByText(/refuses anything not BILLING_CERTIFIED/i)).toBeVisible();
  });
});
