import { describe, expect, it } from 'vitest';
import {
  isAbsoluteHttpUrl, isActivationEvidenceValid, isOpaqueReference,
  isRecoveryEvidenceValid, isSha256,
} from './dataGovernanceValidation';

const baseRecovery = {
  assetId: '21', scopeKey: 'postgresql.primary', evidenceType: 'BackupSetObserved' as const,
  providerReference: 'provider-project-9', backupSetReference: 'backup-20260808',
  recoveryPoint: '2026-08-08T10:00', operationStarted: '', completed: '2026-08-08T11:00',
  configuredRpoSeconds: '', configuredRtoSeconds: '', retainUntil: '2026-09-08T10:00',
  customerRowsObserved: '', evidenceReference: 'evidence-backup-9', evidenceSha256: 'a'.repeat(64),
  correlationId: 'recovery-9', idempotencyKey: 'recovery-9', reason: 'Provider inventory checked',
};

describe('platform data-governance evidence validation', () => {
  it('keeps credentials and connection strings out of opaque references', () => {
    expect(isOpaqueReference('provider-project-9')).toBe(true);
    expect(isOpaqueReference('postgres://user:secret@host/database')).toBe(false);
    expect(isOpaqueReference('token=secret')).toBe(false);
  });

  it('requires a governed HTTP(S) URL and an exact digest for activation evidence', () => {
    expect(isAbsoluteHttpUrl('https://evidence.example/control/9')).toBe(true);
    expect(isAbsoluteHttpUrl('javascript:alert(1)')).toBe(false);
    expect(isSha256('a'.repeat(64))).toBe(true);
    expect(isActivationEvidenceValid({
      evidenceReference: 'https://evidence.example/control/9', evidenceSha256: 'a'.repeat(64),
      effectiveFrom: '2026-08-08T10:00', effectiveTo: '2026-08-08T09:00', reason: 'Approved',
    })).toBe(false);
  });

  it('requires backup identity, recovery point and retention before a backup observation', () => {
    expect(isRecoveryEvidenceValid(baseRecovery)).toBe(true);
    expect(isRecoveryEvidenceValid({ ...baseRecovery, backupSetReference: '' })).toBe(false);
    expect(isRecoveryEvidenceValid({ ...baseRecovery, retainUntil: '' })).toBe(false);
  });

  it('requires measured RPO/RTO inputs and zero rows for non-resurrection evidence', () => {
    expect(isRecoveryEvidenceValid({
      ...baseRecovery, evidenceType: 'RestoreDrillCompleted', backupSetReference: '', retainUntil: '',
      operationStarted: '2026-08-08T10:10', configuredRpoSeconds: '3600', configuredRtoSeconds: '1800',
      customerRowsObserved: '12',
    })).toBe(true);
    expect(isRecoveryEvidenceValid({
      ...baseRecovery, evidenceType: 'TombstoneReapplied', backupSetReference: '', recoveryPoint: '',
      retainUntil: '', customerRowsObserved: '1',
    })).toBe(false);
    expect(isRecoveryEvidenceValid({
      ...baseRecovery, evidenceType: 'TombstoneReapplied', backupSetReference: '', recoveryPoint: '',
      retainUntil: '', customerRowsObserved: '0',
    })).toBe(true);
  });
});
