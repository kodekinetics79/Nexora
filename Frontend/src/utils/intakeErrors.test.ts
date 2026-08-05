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
      'macro_enabled_document',
      'malware_detected',
      'unsupported_format',
      'security_scan_cleared',
      'ingestion_failed',
    ]) {
      expect(hasIntakeErrorExplanation(code)).toBe(true);
    }
  });

  it('treats the macro block as terminal, matching the backend', () => {
    // DocumentInspectionErrorCodes.MacroEnabledDocument is absent from
    // SecurityHoldRecovery.RecoverableErrorCodes: no replay will ever un-macro a workbook.
    expect(isRecoverableIntakeErrorCode('macro_enabled_document')).toBe(false);
    expect(explainIntakeError('macro_enabled_document').isRetryable).toBe(false);
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
      'macro_enabled_document',
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

/**
 * The bug the owner hit: `document_rejected` is a BUCKET code covering at least a dozen materially
 * different verdicts, and it was answered with one static sentence — "its contents do not match a
 * document type we can process, or the file is damaged. Re-export or re-save the document and
 * upload it again. If it opens correctly for you, send it as a PDF."
 *
 * For the macro-enabled workbook they actually uploaded, every clause of that was wrong. The
 * backend had computed and persisted the true reason the whole time; the UI discarded it.
 */
describe('the server reason outranks our guess', () => {
  const MACRO_REASON =
    'This workbook contains macros (embedded VBA code), which Nexora does not accept. ' +
    'Open it in Excel, use Save As to keep a macro-free copy as .xlsx, and upload that, ' +
    'or ask the sender for a macro-free version.';

  it('renders the server reason instead of the generic document_rejected guess', () => {
    const explanation = explainIntakeError('document_rejected', MACRO_REASON);

    expect(explanation.whatHappened).toBe(MACRO_REASON);
    expect(explanation.isInferred).toBe(false);
    // The falsehoods the owner was told are gone.
    const prose = `${explanation.whatHappened} ${explanation.nextAction}`;
    expect(prose).not.toMatch(/damaged/i);
    expect(prose).not.toMatch(/send it as a PDF/i);
  });

  it('falls back to the static sentence ONLY when the server gave nothing', () => {
    for (const nothing of [undefined, null, '', '   ']) {
      const explanation = explainIntakeError('document_rejected', nothing);
      expect(explanation.isInferred).toBe(true);
      expect(explanation.whatHappened).toContain('no specific reason reached this screen');
    }
  });

  it('never asserts a cause it was not told — the fallback claims only what it knows', () => {
    const fallback = explainIntakeError('document_rejected');
    const prose = `${fallback.title} ${fallback.whatHappened} ${fallback.nextAction}`;
    // "damaged" and "does not match a document type we can process" were inventions about a
    // verdict this code does not identify.
    expect(prose).not.toMatch(/damaged/i);
    expect(prose).not.toMatch(/send it as a PDF/i);
  });

  it('keeps the unknown-code fallback open to a server reason', () => {
    const explained = explainIntakeError('some_future_code', 'The file exceeds the 25 MB inspection limit.');
    expect(explained.whatHappened).toBe('The file exceeds the 25 MB inspection limit.');
    expect(explained.isInferred).toBe(false);

    const bare = explainIntakeError('some_future_code');
    expect(bare.isInferred).toBe(true);
    expect(bare.title).toBe('This file needs attention');
  });

  it('does not let a server one-liner displace copy that says more', () => {
    // "Malware was detected." is true but says less than our copy, and the code already pins the
    // cause exactly — so here the static sentence stays.
    const explanation = explainIntakeError('malware_detected', 'Malware was detected.');
    expect(explanation.whatHappened).toContain('identified malicious content');
    expect(explanation.nextAction).toContain('Do not re-upload');
  });

  it('reads the reason off a batch item and reports the same thing the upload row does', () => {
    const fromBatch = explainIntakeItem({
      errorCode: 'macro_enabled_document',
      intakeStatus: 'Rejected',
      recoverableSecurityHold: false,
      reasons: [MACRO_REASON],
    });
    const fromUpload = explainIntakeError('macro_enabled_document', MACRO_REASON);

    expect(fromBatch.whatHappened).toBe(MACRO_REASON);
    expect(fromBatch.whatHappened).toBe(fromUpload.whatHappened);
    expect(fromBatch.nextAction).toBe(fromUpload.nextAction);
    expect(fromBatch.isInferred).toBe(false);
  });

  it('keeps the honest fallback when the item carries no reason at all', () => {
    const explanation = explainIntakeItem({
      errorCode: 'document_rejected',
      recoverableSecurityHold: false,
      reasons: [],
    });
    expect(explanation.isInferred).toBe(true);
  });

  it('does not invent a cause on the verdict-reached downgrade path either', () => {
    const withReason = explainIntakeItem({
      errorCode: 'document_quarantined',
      intakeStatus: 'Rejected',
      recoverableSecurityHold: false,
      reasons: ["The file is named '.xls' but its contents are not in that format."],
    });
    expect(withReason.category).toBe('content');
    expect(withReason.whatHappened).toContain('not in that format');
    expect(withReason.isInferred).toBe(false);

    const withoutReason = explainIntakeItem({
      errorCode: 'document_quarantined',
      intakeStatus: 'Rejected',
      recoverableSecurityHold: false,
    });
    expect(withoutReason.isInferred).toBe(true);
    expect(withoutReason.whatHappened).not.toMatch(/damaged|macro|PDF/i);
  });
});

describe('macro policy copy — reject, but say what to do', () => {
  it('names the cause and the real remedy, and never the wrong one', () => {
    const explanation = explainIntakeError('macro_enabled_document');
    const prose = `${explanation.title} ${explanation.whatHappened} ${explanation.nextAction}`;

    expect(prose).toMatch(/macros/i);
    expect(prose).toContain('.xlsx');
    expect(prose).toContain('.docx');
    expect(prose).not.toMatch(/\bPDF\b/);
    expect(prose).not.toMatch(/re-export/i);
    expect(explanation.category).toBe('content');
    expect(explanation.isRetryable).toBe(false);
  });

  it('is not the generic rejection copy wearing a different code', () => {
    const macro = explainIntakeError('macro_enabled_document');
    const generic = explainIntakeError('document_rejected');
    expect(macro.title).not.toBe(generic.title);
    expect(macro.whatHappened).not.toBe(generic.whatHappened);
    expect(macro.nextAction).not.toBe(generic.nextAction);
  });
});

/**
 * Reason strings are server-authored text. They get exactly the same scrutiny as any other server
 * body: one gate, `presentableServerText` in src/utils/apiErrors.ts. These cases are the failure
 * modes that gate exists for — if intakeErrors ever grows its own weaker copy of the rules, this
 * is what catches it.
 */
describe('the presentability gate still blocks unsafe server text', () => {
  const unsafeReasons = [
    'System.IO.IOException: the inspection pipeline threw',
    'Connection refused to clamd at 10.0.0.4:3310',
    'See https://api.internal.nexora.example/health for details',
    '<html><body>502 Bad Gateway</body></html>',
    'at ERP_RFQ_Automation.Security.Inspect(Byte[] bytes)',
    'Failed reading /var/lib/nexora/quarantine/aa/deadbeef.xls',
    'Bad Request',
    `A reason that runs on and on ${'x'.repeat(400)}`,
    'Contains a control\u0007character',
  ];

  it('never renders operator diagnostics as the explanation', () => {
    for (const reason of unsafeReasons) {
      const explanation = explainIntakeError('document_rejected', reason);
      expect(explanation.whatHappened).not.toBe(reason);
      expect(explanation.isInferred).toBe(true);
      expect(explanation.whatHappened).toContain('no specific reason reached this screen');
    }
  });

  it('rejects non-string reasons rather than rendering [object Object]', () => {
    for (const notAString of [{ reason: 'nested' }, ['a'], 42, true] as unknown[]) {
      const explanation = explainIntakeError('document_rejected', notAString as string);
      expect(explanation.isInferred).toBe(true);
      expect(explanation.whatHappened).not.toContain('object Object');
    }
  });

  it('collapses multi-line server text into one renderable sentence', () => {
    const explanation = explainIntakeError(
      'document_rejected',
      'This workbook contains macros\n(embedded VBA code),\twhich Nexora does not accept.',
    );
    expect(explanation.whatHappened).toBe(
      'This workbook contains macros (embedded VBA code), which Nexora does not accept.',
    );
  });
});
