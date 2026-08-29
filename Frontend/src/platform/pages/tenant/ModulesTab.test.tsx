import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../../api/client';
import type { Tenant, TenantModules } from '../../types';
import ModulesTab from './ModulesTab';

const permissions = { canAdministerBilling: true };
vi.mock('../../auth/usePlatformPermissions', () => ({
  usePlatformPermissions: () => permissions,
}));

const tenant = { id: '9', name: 'Acme', planId: '3', planCode: 'growth' } as Tenant;

const modules = (overrides: Partial<TenantModules> = {}): TenantModules => ({
  tenantId: 9,
  tenantName: 'Acme',
  planId: 3,
  planCode: 'growth',
  modules: [
    { key: 'module.rfq', enabled: true, available: true, fromPlanTemplate: true },
    { key: 'module.quotes', enabled: true, available: true, fromPlanTemplate: true },
    { key: 'module.orders', enabled: false, available: true, fromPlanTemplate: true },
    { key: 'module.procurement', enabled: true, available: true, fromPlanTemplate: false },
    { key: 'module.inventory', enabled: false, available: true, fromPlanTemplate: false },
    { key: 'capability.sso', enabled: false, available: false, fromPlanTemplate: false },
  ],
  ...overrides,
});

const renderTab = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <ModulesTab tenant={tenant} />
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.restoreAllMocks();
  permissions.canAdministerBilling = true;
  vi.spyOn(platformApi, 'getTenantModules').mockResolvedValue(modules());
  vi.spyOn(platformApi, 'listPlans').mockResolvedValue([{
    id: '3', name: 'Growth', code: 'growth', tier: 'growth', weight: 20,
    concurrencyCap: 4, monthlyDocQuota: 1_000, seatQuota: 25,
    priceMonthlyUsd: 499, isActive: true, entitlements: ['module.rfq'],
  }]);
});

describe('ModulesTab', () => {
  it('shows what the runtime enforces for this customer, not what the plan says', async () => {
    renderTab();

    // Procurement is ON while the plan says off, and Inventory is OFF while the plan says on.
    // Both are legitimate per-customer decisions and the screen has to make them visible —
    // spotting a deliberate exception a year later is the whole reason the marker exists.
    expect(await screen.findByText('Added beyond plan')).toBeVisible();
    expect(screen.getByText('Removed from plan')).toBeVisible();
    expect(screen.getByRole('checkbox', { name: 'Orders' })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'RFQs' })).toBeChecked();
  });

  it('refuses to offer a capability that has no product behind it', async () => {
    renderTab();
    expect(await screen.findByText('Not built yet')).toBeVisible();
    expect(screen.getByRole('checkbox', { name: 'SSO' })).toBeDisabled();
  });

  it('will not save without a reason long enough to explain the revoke', async () => {
    renderTab();
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Inventory' }));

    expect(screen.getByText('1 pending change')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Save module access' })).toBeDisabled();

    fireEvent.change(screen.getByRole('textbox', { name: /^Why/ }), { target: { value: 'no' } });
    expect(screen.getByRole('button', { name: 'Save module access' })).toBeDisabled();
  });

  it('sends the WHOLE catalogue, not just the changed key', async () => {
    const update = vi.spyOn(platformApi, 'updateTenantModules').mockResolvedValue(modules());
    renderTab();

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Orders' }));
    fireEvent.change(screen.getByRole('textbox', { name: /^Why/ }), {
      target: { value: 'Customer purchased the orders module on renewal' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save module access' }));

    await waitFor(() => expect(update).toHaveBeenCalledWith(
      '9',
      // Every key present and explicitly decided: the activation policy requires it, and an
      // absent key is not the same as a denied one to that control.
      {
        'module.rfq': true,
        'module.quotes': true,
        'module.orders': true,
        'module.procurement': true,
        'module.inventory': false,
        'capability.sso': false,
      },
      'Customer purchased the orders module on renewal',
    ));
  });

  it('tells a support operator who owns the decision instead of hiding it', async () => {
    permissions.canAdministerBilling = false;
    renderTab();

    expect(await screen.findByText('You can read this, not change it')).toBeVisible();
    // Disabled rather than absent: the operator's real next step is to find whoever can.
    expect(screen.getByRole('checkbox', { name: 'Orders' })).toBeDisabled();
  });
});
