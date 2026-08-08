import type { TenantDataRecoveryEvidenceType } from '../../types';

export const isSha256 = (value: string): boolean => /^[a-fA-F0-9]{64}$/.test(value);

export const isOpaqueReference = (value: string): boolean =>
  value.trim().length > 0 && !/[\s@=?]|:\/\//.test(value);

export const isAbsoluteHttpUrl = (value: string): boolean => {
  try { return Boolean(new URL(value.trim()).protocol.match(/^https?:$/)); } catch { return false; }
};

export interface ActivationEvidenceValues {
  evidenceReference: string;
  evidenceSha256: string;
  effectiveFrom: string;
  effectiveTo: string;
  reason: string;
}

export const isActivationEvidenceValid = (value: ActivationEvidenceValues): boolean =>
  isAbsoluteHttpUrl(value.evidenceReference)
  && isSha256(value.evidenceSha256)
  && Boolean(value.effectiveFrom)
  && (!value.effectiveTo || new Date(value.effectiveTo) > new Date(value.effectiveFrom))
  && Boolean(value.reason.trim());

export interface RecoveryEvidenceValues {
  assetId: string;
  scopeKey: string;
  evidenceType: TenantDataRecoveryEvidenceType;
  providerReference: string;
  backupSetReference: string;
  recoveryPoint: string;
  operationStarted: string;
  completed: string;
  configuredRpoSeconds: string;
  configuredRtoSeconds: string;
  retainUntil: string;
  customerRowsObserved: string;
  evidenceReference: string;
  evidenceSha256: string;
  correlationId: string;
  idempotencyKey: string;
  reason: string;
}

export const isRecoveryEvidenceValid = (value: RecoveryEvidenceValues): boolean => {
  const backup = value.evidenceType === 'BackupSetObserved';
  const restore = value.evidenceType === 'RestoreDrillCompleted';
  const rowsRequired = restore || value.evidenceType === 'TombstoneReapplied';
  return Boolean(value.assetId
    && isOpaqueReference(value.scopeKey)
    && isOpaqueReference(value.providerReference)
    && value.completed
    && isOpaqueReference(value.evidenceReference)
    && isSha256(value.evidenceSha256)
    && isOpaqueReference(value.correlationId)
    && isOpaqueReference(value.idempotencyKey)
    && value.reason.trim()
    && (!backup || (isOpaqueReference(value.backupSetReference) && value.recoveryPoint && value.retainUntil))
    && (!restore || (value.recoveryPoint && value.operationStarted
      && Number(value.configuredRpoSeconds) > 0 && Number(value.configuredRtoSeconds) > 0))
    && (!rowsRequired || /^\d+$/.test(value.customerRowsObserved))
    && (value.evidenceType !== 'TombstoneReapplied' || value.customerRowsObserved === '0'));
};
