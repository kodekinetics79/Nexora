import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../../api/client';
import type { Tenant, TenantOffboardingStatus } from '../../types';
import LifecycleTab from './LifecycleTab';

vi.mock('../../auth/usePlatformPermissions', () => ({
  usePlatformPermissions: () => ({
    role: 'Owner', isOwner: true, canAdministerTenants: true,
    canAdministerBilling: true, canImpersonate: true, roleUnknown: false,
  }),
}));

vi.mock('../../auth/useStepUpReauthentication', () => ({
  useStepUpReauthentication: () => ({ guard: (operation: () => unknown) => operation(), dialog: null }),
  isStepUpCancelled: () => false,
}));

const tenant = {
  id: '42', name: 'Acme', slug: 'acme', status: 'archived',
} as Tenant;

const offboarding = (overrides: Partial<TenantOffboardingStatus> = {}): TenantOffboardingStatus => ({
  tenantId: '42', tenantName: 'Acme', tenantSlug: 'acme', tenantStatus: 'Archived',
  stage: 'NotScheduled', retentionDays: null, deletionScheduledOn: null, purgeEligibleOn: null,
  isPurgeEligible: false, daysUntilPurgeEligible: null, deletionReason: null,
  deletionScheduledBy: null, purgedOn: null, purgedBy: null, purgedRowCount: null,
  personalDataErasedOn: null, personalDataErasedBy: null, erasedIdentityCount: null,
  lastExportedOn: null, lastExportedBy: null, canScheduleDeletion: false,
  canCancelDeletion: false, canPurge: false, canErasePersonalData: true,
  purgeRequiresDifferentApprover: false, deletionApprovedBy: null,
  confirmationRequired: 'Acme', history: [], exports: [], disclosures: [],
  commercialEvidenceRequired: true, canAttestNonCustomer: false,
  nonCustomerAttestedOn: null, nonCustomerAttestedBy: null,
  billingStatementCount: 1, subscriptionInvoiceCount: 1,
  readinessFailures: [
    { code: 'FINAL_BILLING_MISSING', detail: 'Finalize terminal billing.' },
    { code: 'EXPORT_RECEIPT_MISSING', detail: 'Take a new export after archiving.' },
  ],
  ...overrides,
});

const renderTab = (status: TenantOffboardingStatus) => {
  vi.spyOn(platformApi, 'getOffboarding').mockResolvedValue(status);
  vi.spyOn(platformApi, 'listTenantLegalHolds').mockResolvedValue([]);
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <SnackbarProvider><LifecycleTab tenant={tenant} /></SnackbarProvider>
    </QueryClientProvider>,
  );
};

beforeEach(() => vi.restoreAllMocks());

describe('LifecycleTab disabled action guidance', () => {
  it('describes every server-disabled lifecycle action with its next step', async () => {
    renderTab(offboarding());

    const schedule = await screen.findByRole('button', { name: 'Schedule deletion' });
    expect(schedule).toBeDisabled();
    expect(schedule).toHaveAccessibleDescription(/Finalize terminal billing.*Take a new export after archiving/i);

    const cancel = screen.getByRole('button', { name: 'Cancel the scheduled deletion' });
    expect(cancel).toBeDisabled();
    expect(cancel).toHaveAccessibleDescription(/No deletion is scheduled/i);

    const purge = screen.getByRole('button', { name: 'Permanently delete eligible tenant data' });
    expect(purge).toBeDisabled();
    expect(purge).toHaveAccessibleDescription(/Schedule deletion first.*full retention period/i);
  });

  it('connects legal-hold-disabled destruction controls to the release-hold action', async () => {
    vi.spyOn(platformApi, 'getOffboarding').mockResolvedValue(offboarding({
      stage: 'PendingDeletion', canCancelDeletion: true, canPurge: true,
      isPurgeEligible: true, readinessFailures: [],
    }));
    vi.spyOn(platformApi, 'listTenantLegalHolds').mockResolvedValue([{
      id: 'hold-1', tenantId: '42', scope: 'AllTenantData', authority: 'Court order',
      evidenceReference: 'CASE-42', reason: 'Preserve records', placedOn: '2026-08-01T00:00:00Z',
      placedBy: 'owner@example.com', releasedOn: null, releasedBy: null, releaseReason: null,
      isActive: true,
    }]);

    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <SnackbarProvider><LifecycleTab tenant={tenant} /></SnackbarProvider>
      </QueryClientProvider>,
    );

    const purge = await screen.findByRole('button', { name: 'Permanently delete eligible tenant data' });
    expect(await screen.findByText(/Release the active legal hold before permanently deleting/i)).toBeVisible();
    expect(purge).toHaveAccessibleDescription(/Release the active legal hold/i);

    const erase = screen.getByRole('button', { name: 'De-identify people; retain required records' });
    expect(erase).toBeDisabled();
    expect(erase).toHaveAccessibleDescription(/Release the active legal hold/i);
  });

  it('shows the purge-specific server prerequisite after retention has elapsed', async () => {
    renderTab(offboarding({
      stage: 'PendingDeletion', canCancelDeletion: true, canPurge: false,
      isPurgeEligible: true,
      readinessFailures: [{
        code: 'PERSONAL_DATA_ERASURE_MISSING',
        detail: 'Persisted personal-data erasure proof is required before destructive purge.',
      }],
    }));

    const purge = await screen.findByRole('button', { name: 'Permanently delete eligible tenant data' });
    expect(purge).toBeDisabled();
    expect(purge).toHaveAccessibleDescription(/personal-data erasure proof/i);
  });
});
