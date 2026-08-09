import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SnackbarProvider } from 'notistack';
import { platformApi } from '../../api/client';
import type { Tenant } from '../../types';
import BillingGovernanceSection from './BillingGovernanceSection';

vi.mock('../../auth/usePlatformPermissions', () => ({
  usePlatformPermissions: () => ({ isOwner: true, canAdministerBilling: true }),
}));
vi.mock('../../auth/usePlatformAuth', () => ({
  usePlatformAuth: () => ({ platformUser: { email: 'owner@nexora.local', role: 'Owner' } }),
}));

const tenant = { id: '9', name: 'Acme' } as Tenant;
const renderSection = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SnackbarProvider><BillingGovernanceSection tenant={tenant} period="2026-08" /></SnackbarProvider>
  </QueryClientProvider>,
);

beforeEach(() => vi.restoreAllMocks());

describe('BillingGovernanceSection', () => {
  it('renders the closed server catalog and exact readiness failures', async () => {
    vi.spyOn(platformApi, 'listBillingMeterCatalog').mockResolvedValue([
      { eventType: 'documents', billingMeterKey: 'documents', unit: 'document', certification: 'BillingCertified' },
      { eventType: 'storage.gb-hours', billingMeterKey: 'storage.gb', unit: 'gb-hour', certification: 'Blocked' },
    ]);
    vi.spyOn(platformApi, 'getBillingReadiness').mockResolvedValue({
      ready: false,
      failures: [{ code: 'UNCERTIFIED_METER', meterKey: 'storage.gb', detail: 'Meter classification is Blocked.' }],
      manifestJson: '{"server":"evidence"}', manifestSha256: 'a'.repeat(64),
    });
    renderSection();

    expect(await screen.findByText('Billing blocked')).toBeVisible();
    expect(screen.getByText('UNCERTIFIED_METER · storage.gb')).toBeVisible();
    expect(screen.getByText('Meter classification is Blocked.')).toBeVisible();
    expect(screen.getAllByText('documents')).toHaveLength(2);
    expect(screen.getByText('BillingCertified')).toBeVisible();
    expect(screen.getByText('a'.repeat(64))).toBeVisible();
  });

  it('treats a readiness read error as blocked rather than ready', async () => {
    vi.spyOn(platformApi, 'listBillingMeterCatalog').mockResolvedValue([]);
    vi.spyOn(platformApi, 'getBillingReadiness').mockRejectedValue(new Error('offline'));
    renderSection();
    expect(await screen.findByText(/Treat this period as blocked/i)).toBeVisible();
    expect(screen.queryByText('Ready to finalize')).not.toBeInTheDocument();
  });
});
