import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../../api/client';
import type { Tenant, TenantActivationDataDecision, TenantDataAsset } from '../../types';
import DataStorageTab from './DataStorageTab';

const tenant = { id: '9', name: 'Acme', dataRegion: 'us-east-1' } as Tenant;
const boundary = 'This decision certifies only the tenant data-readiness gate. It does not activate the tenant.';
const blocked: TenantActivationDataDecision = {
  tenantId: '9', dataGateReady: false, decision: 'Blocked',
  blockers: ['Primary PostgreSQL tenant-scope asset is not registered.'],
  postgreSqlTenantScope: null, boundary,
};
const asset: TenantDataAsset = {
  id: '21', tenantId: '9', logicalKey: 'postgresql.primary', assetType: 'PostgreSqlTenantScope',
  opaqueProviderReference: 'neon-project-acme', region: 'us-east-1', classification: 'CustomerData',
  disposition: 'BackupRetainedUntilExpiryThenDestroy', backupPolicyReference: 'backup-policy-primary',
  backupPolicyVersion: 2, status: 'Registered', verifiedBusinessUnitId: null,
  verificationEvidenceReference: null, verificationEvidenceSha256: null, verificationVersion: 0,
  verifiedOn: null, verifiedBy: null, version: 1,
};

const renderTab = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SnackbarProvider><DataStorageTab tenant={tenant} /></SnackbarProvider>
  </QueryClientProvider>,
);

beforeEach(() => vi.restoreAllMocks());

describe('DataStorageTab', () => {
  it('shows the authoritative blocker and decision boundary without invented storage totals', async () => {
    vi.spyOn(platformApi, 'listTenantDataAssets').mockResolvedValue([]);
    vi.spyOn(platformApi, 'getTenantActivationDataDecision').mockResolvedValue(blocked);
    renderTab();
    expect(await screen.findByText('Activation blocked')).toBeVisible();
    expect(screen.getByText(blocked.blockers[0])).toBeVisible();
    expect(screen.getByText(boundary)).toBeVisible();
    expect(screen.queryByText(/GB|capacity|storage total/i)).not.toBeInTheDocument();
  });

  it('fails closed when either required read fails', async () => {
    vi.spyOn(platformApi, 'listTenantDataAssets').mockResolvedValue([]);
    vi.spyOn(platformApi, 'getTenantActivationDataDecision').mockRejectedValue(new Error('decision unavailable'));
    renderTab();
    expect(await screen.findByText(/data boundary could not be established/i)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Register boundary' })).not.toBeInTheDocument();
  });

  it('registers only the fixed PostgreSQL contract and refuses connection-string-like references', async () => {
    vi.spyOn(platformApi, 'listTenantDataAssets').mockResolvedValue([]);
    vi.spyOn(platformApi, 'getTenantActivationDataDecision').mockResolvedValue(blocked);
    const register = vi.spyOn(platformApi, 'registerTenantDataAsset').mockResolvedValue(asset);
    renderTab();
    fireEvent.click(await screen.findByRole('button', { name: 'Register boundary' }));
    const submit = screen.getByRole('button', { name: /^Register$/ });
    fireEvent.change(screen.getByLabelText(/Opaque provider reference/), { target: { value: 'postgres://secret@host/db' } });
    fireEvent.change(screen.getByLabelText(/Opaque backup policy reference/), { target: { value: 'backup-policy-primary' } });
    fireEvent.change(screen.getByLabelText(/Registration reason/), { target: { value: 'Register the provisioned tenant boundary' } });
    expect(submit).toBeDisabled();

    fireEvent.change(screen.getByLabelText(/Opaque provider reference/), { target: { value: 'neon-project-acme' } });
    fireEvent.click(submit);
    await waitFor(() => expect(register).toHaveBeenCalledWith('9', expect.objectContaining({
      logicalKey: 'postgresql.primary', classification: 'CustomerData',
      disposition: 'BackupRetainedUntilExpiryThenDestroy', region: 'us-east-1',
    })));
  });
});
