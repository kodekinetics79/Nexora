import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../../api/client';
import type {
  Tenant, TenantActivationDataDecision, TenantActivationDecision, TenantDataAsset,
  TenantDeletionCertificationDecision,
} from '../../types';
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
const activationBlocked: TenantActivationDecision = {
  tenantId: '9', ready: false, commercialState: 'PROSPECT', accessState: 'RESTRICTED',
  dataState: 'LIVE', legalHoldState: 'NONE', policyVersion: 'tenant-activation/2026-08-08.v1',
  evaluatedAtUtc: '2026-08-08T12:00:00Z', warnings: [],
  blockingControls: ['security.privileged-mfa-policy'],
  controls: [{
    code: 'security.privileged-mfa-policy', satisfied: false,
    detail: 'Owner-approved privileged MFA evidence is required.', evidenceReferences: [],
  }],
};
const deletionBlocked: TenantDeletionCertificationDecision = {
  tenantId: '9', ready: false, evidenceIds: [], evaluatedUtc: '2026-08-08T12:00:00Z',
  blockers: ['Tenant database purge has not completed.'],
  boundary: 'Unknown provider state is a blocker, never an implicit deletion success.',
};

const renderTab = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SnackbarProvider><DataStorageTab tenant={tenant} /></SnackbarProvider>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.restoreAllMocks();
  vi.spyOn(platformApi, 'getTenantActivationDecision').mockResolvedValue(activationBlocked);
  vi.spyOn(platformApi, 'listTenantRecoveryEvidence').mockResolvedValue([]);
  vi.spyOn(platformApi, 'getTenantDeletionCertificationDecision').mockResolvedValue(deletionBlocked);
});

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

  it('shows the full activation blockers and never enables a client-side override', async () => {
    vi.spyOn(platformApi, 'listTenantDataAssets').mockResolvedValue([]);
    vi.spyOn(platformApi, 'getTenantActivationDataDecision').mockResolvedValue(blocked);
    renderTab();
    expect(await screen.findByText('Authoritative tenant activation')).toBeVisible();
    expect(screen.getByText('security.privileged-mfa-policy')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Activate tenant' })).toBeDisabled();
    expect(screen.getByText(/server transition changes tenant state/i)).toBeVisible();
  });

  it('requires real evidence metadata before recording an activation attestation', async () => {
    vi.spyOn(platformApi, 'listTenantDataAssets').mockResolvedValue([]);
    vi.spyOn(platformApi, 'getTenantActivationDataDecision').mockResolvedValue(blocked);
    const recordEvidence = vi.spyOn(platformApi, 'recordTenantActivationEvidence').mockResolvedValue({
      tenantId: '9', controlCode: 'security.privileged-mfa-policy', disposition: 'approved',
      evidenceReference: 'https://evidence.example/mfa/9', effectiveFromUtc: '2026-08-08T12:00:00Z',
      effectiveToUtc: null, policyVersion: activationBlocked.policyVersion,
    });
    renderTab();
    fireEvent.click(await screen.findByRole('button', { name: 'Record evidence' }));
    const submit = screen.getByRole('button', { name: 'Record immutable evidence' });
    expect(submit).toBeDisabled();
    fireEvent.change(screen.getByLabelText('Evidence URL'), { target: { value: 'not-a-url' } });
    fireEvent.change(screen.getByLabelText('Evidence SHA-256'), { target: { value: 'bad' } });
    fireEvent.change(screen.getByLabelText('Approval reason'), { target: { value: 'Independent policy review completed' } });
    expect(submit).toBeDisabled();

    fireEvent.change(screen.getByLabelText('Evidence URL'), { target: { value: 'https://evidence.example/mfa/9' } });
    fireEvent.change(screen.getByLabelText('Evidence SHA-256'), { target: { value: 'a'.repeat(64) } });
    fireEvent.click(submit);
    await waitFor(() => expect(recordEvidence).toHaveBeenCalledWith('9', 'security.privileged-mfa-policy',
      expect.objectContaining({ disposition: 'approved', evidenceSha256: 'a'.repeat(64) })));
  });

  it('renders unknown deletion state as a blocker and exposes no certificate mutation', async () => {
    vi.spyOn(platformApi, 'listTenantDataAssets').mockResolvedValue([]);
    vi.spyOn(platformApi, 'getTenantActivationDataDecision').mockResolvedValue(blocked);
    renderTab();
    expect(await screen.findByText('Deletion certification')).toBeVisible();
    expect(screen.getByText('Tenant database purge has not completed.')).toBeVisible();
    expect(screen.getByText(/unknown provider state is a blocker/i)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Issue certificate' })).toBeDisabled();
    expect(screen.queryByText(/backup healthy|backup complete/i)).not.toBeInTheDocument();
  });

  it('records a typed backup observation only with retention and immutable evidence fields', async () => {
    vi.spyOn(platformApi, 'listTenantDataAssets').mockResolvedValue([asset]);
    vi.spyOn(platformApi, 'getTenantActivationDataDecision').mockResolvedValue({
      ...blocked, postgreSqlTenantScope: asset,
    });
    const recordRecovery = vi.spyOn(platformApi, 'recordTenantRecoveryEvidence').mockResolvedValue({
      id: '31', tenantId: '9', tenantDataAssetId: '21', scopeKey: 'postgresql.primary',
      evidenceType: 'BackupSetObserved', opaqueProviderReference: 'neon-project-acme',
      opaqueBackupSetReference: 'backup-set-20260808', recoveryPointUtc: '2026-08-08T10:00:00Z',
      operationStartedUtc: null, completedUtc: '2026-08-08T11:00:00Z', configuredRpoSeconds: null,
      configuredRtoSeconds: null, actualRecoverySeconds: null, retainUntilUtc: '2026-09-08T10:00:00Z',
      customerRowsObserved: null, evidenceReference: 'evidence-backup-20260808', evidenceSha256: 'b'.repeat(64),
      correlationId: 'recovery-31', actorEmail: 'owner@nexora.local', reason: 'Provider backup inventory checked',
      recordedUtc: '2026-08-08T11:01:00Z',
    });
    renderTab();
    fireEvent.click(await screen.findByRole('button', { name: 'Record recovery evidence' }));
    const submit = screen.getAllByRole('button', { name: 'Record immutable evidence' }).at(-1)!;
    expect(submit).toBeDisabled();
    fireEvent.change(screen.getByLabelText('Opaque backup-set reference'), { target: { value: 'backup-set-20260808' } });
    fireEvent.change(screen.getByLabelText('Recovery point'), { target: { value: '2026-08-08T10:00' } });
    fireEvent.change(screen.getByLabelText('Retention expiry'), { target: { value: '2026-09-08T10:00' } });
    fireEvent.change(screen.getByLabelText('Opaque evidence reference'), { target: { value: 'evidence-backup-20260808' } });
    fireEvent.change(screen.getByLabelText('Evidence SHA-256'), { target: { value: 'b'.repeat(64) } });
    fireEvent.change(screen.getByLabelText('Evidence reason'), { target: { value: 'Provider backup inventory checked' } });
    fireEvent.click(submit);
    await waitFor(() => expect(recordRecovery).toHaveBeenCalledWith('9', expect.objectContaining({
      tenantDataAssetId: 21, evidenceType: 'BackupSetObserved',
      opaqueBackupSetReference: 'backup-set-20260808', evidenceSha256: 'b'.repeat(64),
    })));
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
