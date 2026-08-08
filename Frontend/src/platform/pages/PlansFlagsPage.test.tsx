import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../api/client';
import {
  ENTITLEMENT_CATALOG, serializeEntitlements, splitPlanEntitlements,
} from '../entitlements';
import type { Plan } from '../types';
import PlansFlagsPage from './PlansFlagsPage';

vi.mock('../auth/usePlatformPermissions', () => ({
  usePlatformPermissions: () => ({
    role: 'Owner', isOwner: true, canAdministerTenants: true,
    canAdministerBilling: true, canImpersonate: true, roleUnknown: false,
  }),
}));

const plan = (overrides: Partial<Plan> = {}): Plan => ({
  id: '2', name: 'Controlled', code: 'controlled', tier: 'controlled', weight: 5,
  concurrencyCap: 2, monthlyDocQuota: 500, seatQuota: 10,
  priceMonthlyUsd: 99, isActive: true, entitlements: [], ...overrides,
});

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SnackbarProvider><PlansFlagsPage /></SnackbarProvider>
  </QueryClientProvider>,
);

beforeEach(() => vi.restoreAllMocks());

describe('typed plan entitlements', () => {
  it('serializes only the exact closed catalogue as boolean JSON', () => {
    const serialized = JSON.parse(serializeEntitlements(['module.rfq', 'capability.ai']));
    expect(Object.keys(serialized)).toHaveLength(ENTITLEMENT_CATALOG.length);
    expect(serialized['module.rfq']).toBe(true);
    expect(serialized['capability.ai']).toBe(true);
    expect(serialized['module.orders']).toBe(false);
    expect(serialized.copilot).toBeUndefined();

    expect(splitPlanEntitlements(['module.rfq', 'unknown.feature'])).toEqual({
      selected: ['module.rfq'], unknown: ['unknown.feature'],
    });
  });

  it('warns explicitly when an editable plan enables no product features', async () => {
    vi.spyOn(platformApi, 'listPlans').mockResolvedValue([plan()]);
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    expect(screen.getByText('No product features enabled')).toBeVisible();
    expect(screen.queryByLabelText(/Features \(JSON object\)/i)).not.toBeInTheDocument();
  });

  it('renders an actionable empty catalogue without inferred defaults', async () => {
    vi.spyOn(platformApi, 'listPlans').mockResolvedValue([]);
    renderPage();
    expect(await screen.findByText('No plans configured')).toBeVisible();
    expect(screen.getByText(/No entitlement defaults are inferred/i)).toBeVisible();
    expect(screen.queryByText('Quota Matrix')).not.toBeInTheDocument();
  });

  it('explains the assignment impact of an inactive plan', async () => {
    vi.spyOn(platformApi, 'listPlans').mockResolvedValue([plan({ isActive: false })]);
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    expect(screen.getByText('Plan inactive')).toBeVisible();
    expect(screen.getByText(/cannot be assigned to another tenant/i)).toBeVisible();
  });
});
