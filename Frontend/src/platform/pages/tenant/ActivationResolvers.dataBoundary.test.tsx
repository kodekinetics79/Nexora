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
  source: 'console',
  observation: {
    host: 'ep-super-sea.c-2.us-east-1.aws.neon.tech', providerName: 'Neon',
    opaqueProviderReference: 'neon-ep-super-sea', region: 'us-east-1',
    basis: 'Read from the database host this process is connected to.', isUsable: true,
  },
  recordedBy: 'owner@nexora.app',
  recordedOn: '2026-09-06T10:00:00Z',
  recordedBasis: 'observed-and-confirmed',
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
   * The state an operator actually meets on a fresh deployment: nothing declared anywhere. What
   * they must NOT be shown is four opaque infrastructure fields, and what they must NOT be told is
   * to go and set an environment variable — the first fix for this control did the second, which
   * is the same demand wearing a different hat. The server is connected to the database in
   * question, so it says what it is and asks for a confirmation.
   */
  it('offers what the server read about its own database instead of asking for it', async () => {
    vi.spyOn(platformApi, 'getPlatformDataBoundaries').mockResolvedValue({
      ...manifest, configured: false, source: 'none', primaryPostgreSqlScope: null,
    });
    const record = vi.spyOn(platformApi, 'recordPlatformDataBoundary').mockResolvedValue(manifest);
    renderDialog();

    const use = await screen.findByRole('button', { name: 'Use this for every tenant' });
    expect(screen.getByText(/neon-ep-super-sea/)).toBeVisible();
    expect(screen.getByText(/Read from the database host/)).toBeVisible();
    // The two facts the server can see are not asked for at all.
    expect(screen.queryByLabelText(/Opaque provider reference/)).toBeNull();
    expect(screen.queryByLabelText(/^Data region/)).toBeNull();

    fireEvent.click(use);
    await waitFor(() => expect(record).toHaveBeenCalledWith(expect.objectContaining({
      // Omitted, so the server records what IT observed rather than what a form carried back.
      opaqueProviderReference: null,
      region: null,
      backupPolicyReference: 'pitr-7d',
    })));
  });

  /**
   * The one fact no connection can reveal. Backup retention lives in the provider's console, not
   * in the database, so it is asked — once, in words, with the common answer preselected.
   */
  it('asks only for the one thing the server cannot observe', async () => {
    vi.spyOn(platformApi, 'getPlatformDataBoundaries').mockResolvedValue({
      ...manifest, configured: false, source: 'none', primaryPostgreSqlScope: null,
    });
    renderDialog();

    await screen.findByRole('button', { name: 'Use this for every tenant' });
    expect(screen.getByLabelText(/How long backups are kept/)).toBeVisible();
  });

  /**
   * A host that says nothing about itself — a self-hosted box, an IP address — still has to be
   * describable. The panel then asks, in an operator's words rather than the registry's.
   */
  it('asks in plain words when the host tells it nothing', async () => {
    vi.spyOn(platformApi, 'getPlatformDataBoundaries').mockResolvedValue({
      ...manifest,
      configured: false,
      source: 'none',
      primaryPostgreSqlScope: null,
      observation: {
        host: 'db.internal', providerName: null, opaqueProviderReference: null, region: null,
        basis: 'The database host is db.internal. Its shape is not one this deployment can read a provider or a region from, so both have to be stated.',
        isUsable: false,
      },
    });
    renderDialog();

    expect(await screen.findByLabelText(/A name for this database/)).toBeVisible();
    expect(screen.getByLabelText(/Where it is hosted/)).toBeVisible();
    expect(screen.getByText(/could not read its own database name/i)).toBeVisible();
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
