import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../../api/client';
import type { Tenant } from '../../types';
import EntitlementsTab from './EntitlementsTab';

const tenant = { id: '9', name: 'Acme', planId: '3', planCode: 'growth' } as Tenant;
const renderTab = (value = tenant) => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <EntitlementsTab tenant={value} />
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.restoreAllMocks();
  vi.spyOn(platformApi, 'listPlans').mockResolvedValue([{
    id: '3', name: 'Growth', code: 'growth', tier: 'growth', weight: 20,
    concurrencyCap: 4, monthlyDocQuota: 1_000, seatQuota: 25,
    priceMonthlyUsd: 499, isActive: true, entitlements: ['copilot', 'advanced_quotes'],
  }]);
});

describe('EntitlementsTab', () => {
  it('renders only the assigned plan quotas and server-enabled features', async () => {
    renderTab();
    expect(await screen.findByText('Growth entitlements')).toBeVisible();
    expect(screen.getByText('1,000')).toBeVisible();
    expect(screen.getByText('copilot')).toBeVisible();
    expect(screen.getByText('advanced_quotes')).toBeVisible();
  });

  it('does not infer entitlements when the tenant has no plan', async () => {
    renderTab({ ...tenant, planId: null, planCode: null });
    expect(await screen.findByText('This tenant has no plan')).toBeVisible();
    expect(screen.queryByText('copilot')).not.toBeInTheDocument();
  });
});
