import type { TenantOffboardingStatus } from '../../types';

export interface LegalHoldReadiness {
  isLoading: boolean;
  isError: boolean;
  activeHoldCount: number;
}

export interface LifecycleActionBlockers {
  scheduleDeletion: string | null;
  cancelDeletion: string | null;
  purge: string | null;
  erasePersonalData: string | null;
}

const tenantStatus = (status: TenantOffboardingStatus): string =>
  status.tenantStatus.trim().toLowerCase();

const offProductFirst = (status: TenantOffboardingStatus): string =>
  tenantStatus(status) === 'suspended'
    ? 'Archive this tenant first. Scheduling becomes available only after that reversible archive step.'
    : 'Suspend and archive this tenant first. Scheduling becomes available only after those reversible lifecycle steps.';

const legalHoldBlocker = (holds: LegalHoldReadiness, action: 'deletion' | 'de-identification'): string | null => {
  if (holds.isLoading) {
    return `Wait while active legal holds are checked. ${action === 'deletion' ? 'Permanent deletion' : 'Personal-data de-identification'} remains unavailable until verification completes.`;
  }
  if (holds.isError) {
    return `Retry the legal-hold check. The platform fails closed, so ${action === 'deletion' ? 'permanent deletion' : 'personal-data de-identification'} remains unavailable until the hold record can be read.`;
  }
  if (holds.activeHoldCount > 0) {
    return `Release the active legal hold before ${action === 'deletion' ? 'permanently deleting tenant data' : 'de-identifying personal data'}. The hold evidence remains in the audit history.`;
  }
  return null;
};

/**
 * Explains server-resolved lifecycle booleans without reimplementing their authority.
 *
 * The booleans still decide whether a control is enabled. These messages use the accompanying
 * server stage/status/readiness facts only to tell the operator the next valid step.
 */
export const lifecycleActionBlockers = (
  status: TenantOffboardingStatus,
  holds: LegalHoldReadiness,
): LifecycleActionBlockers => {
  const readinessFailures = status.readinessFailures ?? [];
  let scheduleDeletion: string | null = null;
  if (!status.canScheduleDeletion) {
    if (status.stage === 'Purged') {
      scheduleDeletion = 'Tenant data has already been permanently deleted; a new retention window cannot be scheduled.';
    } else if (status.stage === 'PendingDeletion') {
      scheduleDeletion = 'A deletion is already scheduled. Cancel it before starting a new retention window.';
    } else if (tenantStatus(status) !== 'archived') {
      scheduleDeletion = offProductFirst(status);
    } else if (readinessFailures.length > 0) {
      scheduleDeletion = `Complete the server readiness checklist: ${readinessFailures.map((failure) => failure.detail).join(' ')}`;
    } else {
      scheduleDeletion = 'The server has not marked this archived tenant ready. Refresh the offboarding record before trying again.';
    }
  }

  let cancelDeletion: string | null = null;
  if (!status.canCancelDeletion) {
    cancelDeletion = status.stage === 'Purged'
      ? 'Tenant data has already been permanently deleted, so there is no retention clock left to cancel.'
      : 'No deletion is scheduled, so there is no retention clock to cancel.';
  }

  // Explain the first action the operator can actually take. Separation of duties and legal
  // holds matter only after the lifecycle itself is purge-ready; otherwise "ask another Owner"
  // sends that Owner to a control the server will still refuse because the clock is running.
  let purge: string | null = null;
  if (!status.canPurge) {
    if (status.stage === 'Purged') {
      purge = 'Tenant data has already been permanently deleted; the retained audit tombstone cannot be purged.';
    } else if (status.stage === 'NotScheduled') {
      purge = 'Schedule deletion first, then wait for the full retention period and independent Owner approval.';
    } else if (tenantStatus(status) !== 'archived') {
      purge = 'Cancel this scheduled deletion, return the tenant to Archived, then schedule a fresh retention window. A serving tenant is never purged.';
    } else if (!status.isPurgeEligible) {
      purge = status.daysUntilPurgeEligible == null
        ? 'Wait until the server records that the retention period has elapsed.'
        : `Wait ${status.daysUntilPurgeEligible} day(s) for the retention period to elapse. Cancel the deletion if the decision has changed.`;
    } else if (readinessFailures.length > 0) {
      purge = `Complete the server purge-readiness checklist: ${readinessFailures.map((failure) => failure.detail).join(' ')}`;
    } else {
      purge = 'The server has not marked this tenant eligible for permanent deletion. Refresh the offboarding record before trying again.';
    }
  } else {
    purge = legalHoldBlocker(holds, 'deletion');
    if (!purge && status.purgeRequiresDifferentApprover) {
      purge = 'Ask a different Platform Owner to approve permanent deletion. The Owner who scheduled it cannot also execute it.';
    }
  }

  // Service/lifecycle state is likewise the immediate prerequisite for erasure. Do not make an
  // active tenant wait on a legal-hold read when the next valid step is to suspend service.
  let erasePersonalData: string | null = null;
  if (!status.canErasePersonalData) {
    if (status.stage === 'Purged') {
      erasePersonalData = 'Tenant data has already been permanently deleted; only the governed audit tombstone remains.';
    } else {
      erasePersonalData = 'Suspend this tenant first. De-identification disables every user and is unavailable while the customer is being served.';
    }
  } else {
    erasePersonalData = legalHoldBlocker(holds, 'de-identification');
  }

  return { scheduleDeletion, cancelDeletion, purge, erasePersonalData };
};
