import { describe, expect, it } from 'vitest';
import type { TenantOffboardingStatus } from '../../types';
import { lifecycleActionBlockers } from './lifecycleActionGuidance';

const status = (overrides: Partial<TenantOffboardingStatus> = {}): TenantOffboardingStatus => ({
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
  billingStatementCount: 0, subscriptionInvoiceCount: 0, readinessFailures: [],
  ...overrides,
});

const clearHolds = { isLoading: false, isError: false, activeHoldCount: 0 };

describe('lifecycle action guidance', () => {
  it('uses the exact server readiness details as the schedule next action', () => {
    const result = lifecycleActionBlockers(status({
      readinessFailures: [
        { code: 'FINAL_BILLING_MISSING', detail: 'Finalize terminal billing.' },
        { code: 'EXPORT_RECEIPT_MISSING', detail: 'Take a new export after archiving.' },
      ],
    }), clearHolds);

    expect(result.scheduleDeletion).toContain('Finalize terminal billing.');
    expect(result.scheduleDeletion).toContain('Take a new export after archiving.');
    expect(result.cancelDeletion).toMatch(/No deletion is scheduled/);
    expect(result.purge).toMatch(/Schedule deletion first/);
  });

  it('distinguishes retention, independent approval, and legal-hold blockers', () => {
    expect(lifecycleActionBlockers(status({
      stage: 'PendingDeletion', canCancelDeletion: true, daysUntilPurgeEligible: 4,
    }), clearHolds).purge).toMatch(/Wait 4 day/);

    expect(lifecycleActionBlockers(status({
      stage: 'PendingDeletion', canCancelDeletion: true, canPurge: true,
      isPurgeEligible: true, purgeRequiresDifferentApprover: true,
    }), clearHolds).purge).toMatch(/different Platform Owner/);

    const held = lifecycleActionBlockers(status({
      stage: 'PendingDeletion', canCancelDeletion: true, canPurge: true,
      canErasePersonalData: true, isPurgeEligible: true,
    }), { isLoading: false, isError: false, activeHoldCount: 1 });
    expect(held.purge).toMatch(/Release the active legal hold/);
    expect(held.erasePersonalData).toMatch(/Release the active legal hold/);

    expect(lifecycleActionBlockers(status({
      tenantStatus: 'Active', canErasePersonalData: false,
    }), clearHolds).erasePersonalData).toMatch(/Suspend this tenant first/);
  });

  it('reports the immediate lifecycle prerequisite before later approval or hold gates', () => {
    const beforeRetention = lifecycleActionBlockers(status({
      stage: 'PendingDeletion', daysUntilPurgeEligible: 4,
      purgeRequiresDifferentApprover: true,
    }), { isLoading: false, isError: false, activeHoldCount: 1 });
    expect(beforeRetention.purge).toMatch(/Wait 4 day/);
    expect(beforeRetention.purge).not.toMatch(/different Platform Owner|legal hold/i);

    const servingTenant = lifecycleActionBlockers(status({
      tenantStatus: 'Active', canErasePersonalData: false,
    }), { isLoading: true, isError: false, activeHoldCount: 0 });
    expect(servingTenant.erasePersonalData).toMatch(/Suspend this tenant first/);

    const readyButHeld = lifecycleActionBlockers(status({
      stage: 'PendingDeletion', canPurge: true, isPurgeEligible: true,
      purgeRequiresDifferentApprover: true,
    }), { isLoading: false, isError: false, activeHoldCount: 1 });
    expect(readyButHeld.purge).toMatch(/Release the active legal hold/);
  });

  it('surfaces purge-phase readiness after the retention clock has elapsed', () => {
    const result = lifecycleActionBlockers(status({
      stage: 'PendingDeletion', canCancelDeletion: true, canPurge: false,
      isPurgeEligible: true,
      readinessFailures: [
        {
          code: 'PERSONAL_DATA_ERASURE_MISSING',
          detail: 'Persisted personal-data erasure proof is required before destructive purge.',
        },
      ],
    }), clearHolds);

    expect(result.purge).toMatch(/purge-readiness checklist/i);
    expect(result.purge).toMatch(/personal-data erasure proof/i);
  });
});
