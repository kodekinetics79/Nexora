import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../../api/client';
import type { Tenant, TenantActivationDecision, TenantOffboardingStatus } from '../../types';
import ActivationPolicyPanel from './ActivationPolicyPanel';
import LifecycleTab from './LifecycleTab';

/**
 * These two behaviour tests used to live in DataStorageTab.test.tsx, because the panel used to be
 * rendered on the "Data & storage" tab — next to the data-asset registry that satisfies one of its
 * controls. That is defensible from the inside and invisible from the outside: an operator looking
 * for "activate this tenant" opens Lifecycle, finds no Activate button, and concludes the tenant is
 * still provisioning. Noor Sons sat in Provisioning for three days with all eight provisioning
 * steps recorded Succeeded for exactly that reason.
 *
 * So the tests moved with the panel, and they now render the panel DIRECTLY rather than through
 * whichever tab happens to host it — the behaviour they assert is the panel's, not the tab's. The
 * placement itself is pinned separately, at the bottom of this file, because "which screen is this
 * on" turned out to be the property that actually failed.
 */

const permission = vi.hoisted(() => ({ isOwner: true }));
vi.mock('../../auth/usePlatformPermissions', () => ({
  usePlatformPermissions: () => ({
    role: permission.isOwner ? 'Owner' : 'SupportAdmin', isOwner: permission.isOwner,
    canAdministerTenants: true, canAdministerBilling: permission.isOwner,
    canImpersonate: permission.isOwner, roleUnknown: false,
  }),
}));

// `status` matters: the server refuses a profile change once a tenant has left Provisioning, and
// the panel disables the control on the same rule.
const tenant = { id: '9', name: 'Acme', dataRegion: 'us-east-1', status: 'provisioning' } as Tenant;

const activationBlocked: TenantActivationDecision = {
  tenantId: '9', ready: false, commercialState: 'PROSPECT', accessState: 'RESTRICTED',
  dataState: 'LIVE', legalHoldState: 'NONE', policyVersion: 'tenant-activation/2026-08-08.v1',
  evaluatedAtUtc: '2026-08-08T12:00:00Z', warnings: [],
  blockingControls: ['security.privileged-mfa-policy'],
  controls: [{
    code: 'security.privileged-mfa-policy', satisfied: false,
    detail: 'Owner-approved privileged MFA evidence is required.', evidenceReferences: [],
    disposition: 'BLOCKING', blocksProduction: true, deferralKey: null, productionRequirement: null,
  }],
  // A PRODUCTION tenant: nothing is deferrable, so blocking and production-blocking agree.
  deploymentProfile: 'PRODUCTION',
  deploymentProfileDetail: 'PRODUCTION: every activation control is a hard gate and nothing is deferrable.',
  productionBlockingControls: ['security.privileged-mfa-policy'],
  deferredControls: [],
  externallyBlockedControls: [],
  productionReadiness: {
    certifiable: false,
    blockingControls: ['security.privileged-mfa-policy'],
    prerequisites: [],
    detail: 'Not certifiable.',
  },
};

const renderPanel = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SnackbarProvider><ActivationPolicyPanel tenant={tenant} /></SnackbarProvider>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.restoreAllMocks();
  permission.isOwner = true;
  vi.spyOn(platformApi, 'getTenantActivationDecision').mockResolvedValue(activationBlocked);
});

describe('ActivationPolicyPanel', () => {
  it('shows the full activation blockers and never enables a client-side override', async () => {
    renderPanel();
    expect(await screen.findByText('Authoritative tenant activation')).toBeVisible();
    expect(screen.getAllByText('security.privileged-mfa-policy')).toHaveLength(2);
    expect(screen.getByRole('button', { name: 'Activate tenant' })).toBeDisabled();
    expect(screen.getByText(/server transition changes tenant state/i)).toBeVisible();
  });

  it('requires real evidence metadata before recording an activation attestation', async () => {
    const recordEvidence = vi.spyOn(platformApi, 'recordTenantActivationEvidence').mockResolvedValue({
      tenantId: '9', controlCode: 'security.privileged-mfa-policy', disposition: 'approved',
      evidenceReference: 'https://evidence.example/mfa/9', effectiveFromUtc: '2026-08-08T12:00:00Z',
      effectiveToUtc: null, policyVersion: activationBlocked.policyVersion,
    });
    renderPanel();
    fireEvent.click(await screen.findByRole('button', { name: 'Record evidence' }));
    const evidenceDialog = within(await screen.findByRole('dialog', { name: 'Record activation control evidence' }));
    const submit = evidenceDialog.getByRole('button', { name: 'Record immutable evidence' });
    expect(submit).toBeDisabled();
    fireEvent.change(await evidenceDialog.findByLabelText(/Evidence URL/), { target: { value: 'not-a-url' } });
    fireEvent.change(evidenceDialog.getByLabelText(/Evidence SHA-256/), { target: { value: 'bad' } });
    fireEvent.change(evidenceDialog.getByLabelText(/Approval reason/), { target: { value: 'Independent policy review completed' } });
    expect(submit).toBeDisabled();

    fireEvent.change(evidenceDialog.getByLabelText(/Evidence URL/), { target: { value: 'https://evidence.example/mfa/9' } });
    fireEvent.change(evidenceDialog.getByLabelText(/Evidence SHA-256/), { target: { value: 'a'.repeat(64) } });
    fireEvent.click(submit);
    await waitFor(() => expect(recordEvidence).toHaveBeenCalledWith('9', 'security.privileged-mfa-policy',
      expect.objectContaining({ disposition: 'approved', evidenceSha256: 'a'.repeat(64) })));
  });

  /**
   * The deployment profile had a server endpoint, a request DTO, a frontend type and a client
   * method — and no control. Everything existed except the thing an operator could click, so a
   * demo tenant whose externally-supplied prerequisites can never be satisfied had no reachable
   * path to activation. These tests exist because "the API supports it" was already true while the
   * feature was unusable.
   */
  it('offers the deployment profile control and refuses a reason the server would reject', async () => {
    const setProfile = vi.spyOn(platformApi, 'setTenantDeploymentProfile')
      .mockResolvedValue({ ...tenant, deploymentProfile: 'DEMO' } as Tenant);
    renderPanel();

    fireEvent.click(await screen.findByRole('button', { name: 'Change profile' }));
    const dialog = within(await screen.findByRole('dialog', { name: /Deployment profile for Acme/ }));
    const submit = dialog.getByRole('button', { name: 'Record profile' });

    // PRODUCTION is the current profile and needs no reason, so the button is live immediately.
    expect(submit).toBeEnabled();

    fireEvent.mouseDown(dialog.getByLabelText('Profile'));
    fireEvent.click(await screen.findByRole('option', { name: 'DEMO' }));

    // Off PRODUCTION a reason becomes mandatory, and anything under 15 characters is exactly what
    // the server answers 400 to — so the form must refuse it rather than discover it on submit.
    expect(submit).toBeDisabled();
    fireEvent.change(dialog.getByLabelText(/Reason/), { target: { value: 'too short' } });
    expect(submit).toBeDisabled();

    fireEvent.change(dialog.getByLabelText(/Reason/), { target: { value: 'Demonstration tenant with no customer data' } });
    expect(submit).toBeEnabled();
    fireEvent.click(submit);

    await waitFor(() => expect(setProfile).toHaveBeenCalledWith('9', {
      profile: 'DEMO', reason: 'Demonstration tenant with no customer data',
    }));
  });

  it('does not let a non-Owner change the deployment profile', async () => {
    permission.isOwner = false;
    renderPanel();
    expect(await screen.findByRole('button', { name: 'Change profile' })).toBeDisabled();
  });

  /**
   * The placement pin. Activation is the first lifecycle transition a tenant makes, and this is the
   * only screen that names the blocking controls or carries the Activate button — so if it is not
   * on Lifecycle, an operator cannot find it, which is the failure this test exists to catch. It
   * renders the real tab rather than asserting on an import, because an unused import type-checks
   * and still renders nothing.
   */
  it('is rendered on the Lifecycle tab, where activation is looked for', async () => {
    // A complete Live status. The arrays matter: LifecycleTab reads history/exports/disclosures
    // eagerly, so a partial stub crashes the render and the test would fail for a reason that has
    // nothing to do with where the panel is mounted.
    vi.spyOn(platformApi, 'getOffboarding').mockResolvedValue({
      tenantId: '9', tenantName: 'Acme', tenantSlug: 'acme', tenantStatus: 'Provisioning',
      stage: 'Live',
      retentionDays: null, deletionScheduledOn: null, purgeEligibleOn: null, isPurgeEligible: false,
      daysUntilPurgeEligible: null, deletionReason: null, deletionScheduledBy: null,
      purgedOn: null, purgedBy: null, purgedRowCount: null,
      personalDataErasedOn: null, personalDataErasedBy: null, erasedIdentityCount: null,
      lastExportedOn: null, lastExportedBy: null,
      canScheduleDeletion: false, canCancelDeletion: false, canPurge: false,
      canErasePersonalData: false, purgeRequiresDifferentApprover: false,
      deletionApprovedBy: null, confirmationRequired: 'Acme',
      history: [], exports: [], disclosures: [],
    } as unknown as TenantOffboardingStatus);
    vi.spyOn(platformApi, 'listTenantLegalHolds').mockResolvedValue([]);

    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <SnackbarProvider><LifecycleTab tenant={tenant} /></SnackbarProvider>
      </QueryClientProvider>,
    );

    expect(await screen.findByText('Authoritative tenant activation')).toBeVisible();
    expect(await screen.findByRole('button', { name: 'Activate tenant' })).toBeInTheDocument();
  });
});
