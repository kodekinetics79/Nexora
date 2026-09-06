import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../../api/client';
import type { PlatformDataBoundaryManifest, Tenant, TenantActivationDataDecision } from '../../types';
import ActivationResolverDialog from './ActivationResolvers';

/**
 * `data.residency-isolation` was the last activation control that asked an operator to describe
 * the platform to the platform: four opaque infrastructure fields and, after them, a SHA-256 of an
 * evidence document about a database Nexora runs itself. A tenant sat unactivatable while somebody
 * worked out what to put in "opaque provider reference".
 *
 * These tests pin the rule that replaced it — where the deployment has declared its own estate the
 * dialog has no fields at all — and, just as importantly, the two cases where the manual form must
 * still be exactly what it was.
 */

const tenant = { id: '5', name: 'Intelliflow Systems', dataRegion: 'us-east-1' } as Tenant;

const manifest: PlatformDataBoundaryManifest = {
  configured: true,
  primaryPostgreSqlScope: {
    assetType: 'PostgreSqlTenantScope',
    logicalKey: 'postgresql.primary',
    opaqueProviderReference: 'neon-project-nexora-prod',
    region: 'us-east-1',
    classification: 'CustomerData',
    disposition: 'BackupRetainedUntilExpiryThenDestroy',
    backupPolicyReference: 'neon-pitr-7d',
    backupPolicyVersion: 3,
  },
  boundaries: [],
  defects: [],
  configurationKey: 'Platform:DataBoundaries',
};

const decision: TenantActivationDataDecision = {
  tenantId: '5', dataGateReady: true, decision: 'DataGateReady', blockers: [],
  postgreSqlTenantScope: null, boundary: 'Data readiness only.',
};

const renderDialog = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SnackbarProvider>
      <ActivationResolverDialog
        tenant={tenant}
        action="tenant.data-asset-boundary"
        onClose={() => undefined}
        onResolved={() => undefined}
      />
    </SnackbarProvider>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.restoreAllMocks();
  vi.spyOn(platformApi, 'listTenantDataAssets').mockResolvedValue([]);
});

describe('the data-boundary resolver', () => {
  it('asks for nothing when the deployment has declared its own database', async () => {
    vi.spyOn(platformApi, 'getPlatformDataBoundaries').mockResolvedValue(manifest);
    const apply = vi.spyOn(platformApi, 'applyPlatformDataBoundaries').mockResolvedValue({
      dataRegionRecorded: null, primaryScopeState: 'verified', evidenceReference: 'probe-abc',
      registeredLogicalKeys: ['postgresql.primary'], alreadyRegisteredLogicalKeys: [], decision,
    });
    renderDialog();

    const button = await screen.findByRole('button', { name: 'Register and verify' });
    expect(screen.queryByLabelText(/Opaque provider reference/)).toBeNull();
    expect(screen.queryByLabelText(/Backup policy version/)).toBeNull();
    expect(screen.getByText(/neon-project-nexora-prod/)).toBeVisible();
    expect(screen.getByText(/neon-pitr-7d v3/)).toBeVisible();

    fireEvent.click(button);
    await waitFor(() => expect(apply).toHaveBeenCalledWith('5'));
  });

  /**
   * The manual path is not a legacy branch to be removed; it is the whole product for a
   * deployment that has not described itself. What that operator must NOT get is a form with no
   * explanation of why the platform is asking them for infrastructure facts.
   */
  it('falls back to the manual form, naming the keys that would end it', async () => {
    vi.spyOn(platformApi, 'getPlatformDataBoundaries').mockResolvedValue({
      ...manifest, configured: false, primaryPostgreSqlScope: null,
    });
    renderDialog();

    expect(await screen.findByLabelText(/Opaque provider reference/)).toHaveValue('');
    expect(screen.queryByRole('button', { name: 'Register and verify' })).toBeNull();
    expect(screen.getByText(/has not declared its own database/i)).toBeVisible();
    expect(screen.getByText(/Platform__DataBoundaries__PostgreSqlTenantScope__OpaqueProviderReference/))
      .toBeVisible();
  });

  /**
   * A residency claim is never satisfied by editing the claim. When the tenant's contractual
   * region and the deployment's declaration disagree, the server refuses — and the dialog has to
   * say so before the operator presses a button that reads like it will fix things.
   */
  it('warns rather than silently rewriting a contractual region that disagrees', async () => {
    vi.spyOn(platformApi, 'getPlatformDataBoundaries').mockResolvedValue(manifest);
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <SnackbarProvider>
          <ActivationResolverDialog
            tenant={{ ...tenant, dataRegion: 'me-central-1' } as Tenant}
            action="tenant.data-asset-boundary"
            onClose={() => undefined}
            onResolved={() => undefined}
          />
        </SnackbarProvider>
      </QueryClientProvider>,
    );

    // Waited for deliberately: the loading dialog carries alerts of its own, so asserting before
    // the registry and the manifest have both been read would pass on the wrong screen.
    await screen.findByRole('button', { name: 'Register and verify' });
    const alerts = screen.getAllByRole('alert');
    const disagreement = alerts.find((alert) => /me-central-1/.test(alert.textContent ?? ''));
    expect(disagreement).toBeDefined();
    expect(disagreement).toHaveTextContent(/Profile & access/);
    expect(disagreement).toHaveTextContent(/us-east-1/);
  });
});
