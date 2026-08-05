import { describe, expect, it } from 'vitest';
import {
  RECOVERABLE_INTAKE_ERROR_CODES,
  explainIntakeError,
  explainIntakeItem,
  hasIntakeErrorExplanation,
  isInfrastructureHold,
  isRecoverableIntakeErrorCode,
} from './intakeErrors';

describe('backend contract alignment', () => {
  /**
   * Mirrors SecurityHoldRecovery.RecoverableErrorCodes
   * (Backend/ERP_RFQ_Automation/Extraction/SecurityHoldRecovery.cs:18-22). If the backend list
   * changes, this test is the tripwire — the two disagreeing is what previously let the UI offer a
   * retry that answered `Eligible: 0` and did nothing.
   */
  it('matches SecurityHoldRecovery.RecoverableErrorCodes exactly', () => {
    expect([...RECOVERABLE_INTAKE_ERROR_CODES].sort()).toEqual(
      ['document_quarantined', 'security_scanner_unavailable'].sort(),
    );
  });

  it('has explicit copy for every code the backend emits', () => {
    for (const code of [
      'security_scanner_unavailable',
      'document_quarantined',
      'document_rejected',
      'malware_detected',
      'unsupported_format',
      'security_scan_cleared',
      'ingestion_failed',
    ]) {
      expect(hasIntakeErrorExplanation(code)).toBe(true);
    }
  });

  it('does not treat the ingest-boundary failure as a replayable scanner hold', () => {
    // ExtractionController.cs:139 emits this, but it is absent from
    // SecurityHoldRecovery.RecoverableErrorCodes, so the recovery sweep cannot release it.
    expect(isRecoverableIntakeErrorCode('ingestion_failed')).toBe(false);
    expect(explainIntakeError('ingestion_failed').nextAction).toContain('Upload this file again');
  });

  it('classifies retryable versus terminal the way the backend does', () => {
    expect(isRecoverableIntakeErrorCode('security_scanner_unavailable')).toBe(true);
    expect(isRecoverableIntakeErrorCode('document_quarantined')).toBe(true);
    expect(isRecoverableIntakeErrorCode('document_rejected')).toBe(false);
    expect(isRecoverableIntakeErrorCode('malware_detected')).toBe(false);
    expect(isRecoverableIntakeErrorCode('unsupported_format')).toBe(false);
  });

  it('is case-insensitive, matching the backend OrdinalIgnoreCase comparison', () => {
    expect(isRecoverableIntakeErrorCode('Security_Scanner_Unavailable')).toBe(true);
    expect(isRecoverableIntakeErrorCode('  document_quarantined  ')).toBe(true);
  });
});

describe('explainIntakeError — copy quality', () => {
  it('never echoes the raw machine code back at the user', () => {
    for (const code of [
      'security_scanner_unavailable',
      'document_quarantined',
      'document_rejected',
      'malware_detected',
      'unsupported_format',
    ]) {
      const explanation = explainIntakeError(code);
      const prose = `${explanation.title} ${explanation.whatHappened} ${explanation.nextAction}`;
      expect(prose).not.toContain(code);
      expect(prose).not.toContain('_');
      // Title-cased restatements like "Document Quarantined" are the failure mode this map replaces.
      expect(explanation.title).not.toBe('Document Quarantined');
      expect(explanation.whatHappened.length).toBeGreaterThan(20);
      expect(explanation.nextAction.length).toBeGreaterThan(10);
    }
  });

  it('tells the user an infrastructure hold is not their fault and needs no re-upload', () => {
    const explanation = explainIntakeError('security_scanner_unavailable');
    expect(explanation.category).toBe('infrastructure');
    expect(explanation.isRetryable).toBe(true);
    expect(explanation.whatHappened).toContain('Nothing is wrong with your document');
    expect(explanation.nextAction).toContain('No re-upload is needed');
  });

  it('tells the user a malware detection is terminal', () => {
    const explanation = explainIntakeError('malware_detected');
    expect(explanation.category).toBe('content');
    expect(explanation.isRetryable).toBe(false);
    expect(explanation.nextAction).toContain('Do not re-upload');
  });

  it('falls back safely for an unknown or missing code', () => {
    for (const code of ['some_future_code', '', '   ', null, undefined]) {
      const explanation = explainIntakeError(code);
      expect(explanation.title).toBe('This file needs attention');
      expect(explanation.isRetryable).toBe(false);
      expect(explanation.whatHappened).toContain('stored safely');
    }
    expect(hasIntakeErrorExplanation('some_future_code')).toBe(false);
  });
});

describe('explainIntakeItem — the durable recoverableSecurityHold flag wins', () => {
  it('gives infrastructure framing when the backend flags a recoverable hold', () => {
    const explanation = explainIntakeItem({
      errorCode: 'security_scanner_unavailable',
      intakeStatus: 'Rejected',
      recoverableSecurityHold: true,
    });
    expect(explanation.category).toBe('infrastructure');
    expect(explanation.isRetryable).toBe(true);
  });

  it('forces infrastructure framing even for a code with no copy', () => {
    const explanation = explainIntakeItem({
      errorCode: 'some_future_scanner_code',
      recoverableSecurityHold: true,
    });
    expect(explanation.category).toBe('infrastructure');
    expect(explanation.isRetryable).toBe(true);
  });

  it('downgrades a quarantine that reached a real verdict to a terminal content rejection', () => {
    // SecurityHoldRecovery also inspects the recorded ScannerSignature: a document_quarantined
    // occurrence carrying a signature got a genuine verdict and is NOT replayable, so the backend
    // reports recoverableSecurityHold: false even though the bare code is in the recoverable list.
    const explanation = explainIntakeItem({
      errorCode: 'document_quarantined',
      intakeStatus: 'Rejected',
      recoverableSecurityHold: false,
    });
    expect(explanation.category).toBe('content');
    expect(explanation.isRetryable).toBe(false);
    expect(explanation.nextAction).not.toContain('No re-upload is needed');
  });

  it('leaves a genuine content rejection alone', () => {
    const explanation = explainIntakeItem({
      errorCode: 'document_rejected',
      intakeStatus: 'Rejected',
      recoverableSecurityHold: false,
    });
    expect(explanation.category).toBe('content');
    expect(explanation.title).toContain('Rejected');
  });
});

describe('isInfrastructureHold — drives whether the retry control renders', () => {
  it('trusts the durable flag in both directions', () => {
    expect(isInfrastructureHold({ recoverableSecurityHold: true, intakeStatus: 'Rejected' })).toBe(true);
    expect(isInfrastructureHold({
      recoverableSecurityHold: false,
      intakeStatus: 'AwaitingSecurityScan',
    })).toBe(false);
  });

  it("keeps the retry route open for the owner's 8 documents: Rejected + a recoverable code", () => {
    // This is the exact stranded state — occurrences flipped to Rejected, so the old
    // `awaitingSecurityScan > 0` gate unmounted the only recovery control.
    expect(isInfrastructureHold({
      errorCode: 'security_scanner_unavailable',
      intakeStatus: 'Rejected',
      recoverableSecurityHold: true,
    })).toBe(true);
  });

  it('falls back to status and code when the flag is absent (older payloads)', () => {
    expect(isInfrastructureHold({ intakeStatus: 'AwaitingSecurityScan' })).toBe(true);
    expect(isInfrastructureHold({ errorCode: 'document_quarantined', intakeStatus: 'Rejected' })).toBe(true);
    expect(isInfrastructureHold({ errorCode: 'document_rejected', intakeStatus: 'Rejected' })).toBe(false);
    expect(isInfrastructureHold({ errorCode: 'malware_detected', intakeStatus: 'Rejected' })).toBe(false);
  });

  it('does not treat a healthy reconciled item as held', () => {
    expect(isInfrastructureHold({ intakeStatus: 'Reconciled', errorCode: null })).toBe(false);
    expect(isInfrastructureHold({})).toBe(false);
  });
});
