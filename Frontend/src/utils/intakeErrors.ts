/**
 * Intake error-code map.
 *
 * Machine codes are not product copy. Title-casing `document_quarantined` into "Document
 * Quarantined" tells a user nothing about what happened or what to do next, so every code the
 * intake pipeline emits is given an explanation and a next action here.
 *
 * CODES ARE NOT INVENTED — each one below is emitted by the backend today:
 *
 *   security_scanner_unavailable
 *     Backend/ERP_RFQ_Automation/Security/DocumentInspection/DocumentFileInspectionService.cs:164
 *     (set together with `IsRetryable = true`), and re-projected onto every recoverable hold in
 *     LeadIdentity/LeadIdentityApplicationService.cs:326.
 *   document_quarantined / document_rejected
 *     Security/DocumentInspection/DocumentInspectionContracts.cs:46-48 — the default ErrorCode for
 *     the Quarantined and Rejected inspection statuses respectively.
 *   malware_detected
 *     Security/DocumentInspection/DocumentFileInspectionService.cs:153.
 *   unsupported_format
 *     Migrations/20260729221109_PreSecurityDuplicateOccurrenceAccounting.cs:184, bucketed there
 *     alongside document_rejected as UNSUPPORTED_FORMAT.
 *   security_scan_cleared
 *     Security/DocumentInspection/DocumentFileInspectionService.cs:140.
 *
 * The retryable/terminal split mirrors Extraction/SecurityHoldRecovery.cs:18-22, which is the single
 * definition both the reconciliation read model and the recovery endpoint agree on. Keep this list
 * identical to `SecurityHoldRecovery.RecoverableErrorCodes` — when the two disagreed, the UI counted
 * files as recoverable while the recovery call reported `Eligible: 0` and silently did nothing.
 */

/**
 * Why a file stopped.
 *  - `infrastructure`: our scanner never produced a verdict. Not the user's fault, replayable from
 *    the stored source object with no re-upload.
 *  - `content`: a real verdict about a genuinely unusable file. Never replayable.
 *  - `cleared`: inspection passed.
 */
export type IntakeFailureCategory = 'infrastructure' | 'content' | 'cleared';

export interface IntakeErrorExplanation {
  title: string;
  whatHappened: string;
  nextAction: string;
  category: IntakeFailureCategory;
  /** True only when replaying the same stored file could plausibly succeed. */
  isRetryable: boolean;
}

/**
 * Mirror of `SecurityHoldRecovery.RecoverableErrorCodes`
 * (Backend/ERP_RFQ_Automation/Extraction/SecurityHoldRecovery.cs:18-22).
 */
export const RECOVERABLE_INTAKE_ERROR_CODES: readonly string[] = [
  'document_quarantined',
  'security_scanner_unavailable',
];

const INTAKE_ERRORS: Record<string, IntakeErrorExplanation> = {
  security_scanner_unavailable: {
    title: 'Held — malware scanning is offline',
    whatHappened:
      'Our malware scanner did not respond, so this file was never given a verdict. Nothing is wrong with your document, and the original is stored exactly as you sent it.',
    nextAction:
      'No re-upload is needed. This file processes automatically when scanning recovers, or you can retry it now.',
    category: 'infrastructure',
    isRetryable: true,
  },
  document_quarantined: {
    title: 'Held pending a security verdict',
    whatHappened:
      'This file was set aside before inspection finished, so it has no scan result yet. The original is stored safely and unchanged.',
    nextAction:
      'No re-upload is needed. Retry it once scanning is available, or leave it to process automatically.',
    category: 'infrastructure',
    isRetryable: true,
  },
  malware_detected: {
    title: 'Blocked — malware detected',
    whatHappened:
      'Our scanner identified malicious content in this file, so it was blocked and never processed.',
    nextAction:
      'Do not re-upload this file. Check the source with your IT team and request a clean copy from the sender.',
    category: 'content',
    isRetryable: false,
  },
  document_rejected: {
    title: 'Rejected — the file could not be read',
    whatHappened:
      'This file did not pass inspection: its contents do not match a document type we can process, or the file is damaged.',
    nextAction:
      'Re-export or re-save the document and upload it again. If it opens correctly for you, send it as a PDF.',
    category: 'content',
    isRetryable: false,
  },
  unsupported_format: {
    title: 'Rejected — unsupported format',
    whatHappened:
      'This file type is not one Nexora can extract commercial facts from.',
    nextAction:
      'Convert the document to PDF, Word, Excel, CSV or an image, then upload it again.',
    category: 'content',
    isRetryable: false,
  },
  security_scan_cleared: {
    title: 'Security scan passed',
    whatHappened: 'This file was scanned and no malicious content was found.',
    nextAction: 'No action is needed.',
    category: 'cleared',
    isRetryable: false,
  },
  // Controllers/ExtractionController.cs:139 — the ingest boundary's poison-file isolation, so one
  // bad file never fails the whole batch. Not a scanner hold, so the recovery sweep cannot replay
  // it: the way forward is uploading the file again.
  ingestion_failed: {
    title: 'This file could not be accepted',
    whatHappened:
      'Something went wrong while queueing this file, so it never started processing. The rest of the batch was unaffected.',
    nextAction: 'Upload this file again. Contact support with the batch reference if it fails a second time.',
    category: 'content',
    isRetryable: false,
  },
};

const UNKNOWN_INTAKE_ERROR: IntakeErrorExplanation = {
  title: 'This file needs attention',
  whatHappened:
    'Processing stopped for this file before it produced a result. The original document is stored safely and unchanged.',
  nextAction:
    'Retry it, or contact support with the batch reference if it stops again.',
  category: 'content',
  isRetryable: false,
};

const normalizeCode = (code: string | null | undefined): string | null => {
  const trimmed = typeof code === 'string' ? code.trim().toLowerCase() : '';
  return trimmed.length > 0 ? trimmed : null;
};

/** True when `code` is one the backend treats as a replayable scanner-side hold. */
export const isRecoverableIntakeErrorCode = (code: string | null | undefined): boolean => {
  const normalized = normalizeCode(code);
  return normalized !== null && RECOVERABLE_INTAKE_ERROR_CODES.includes(normalized);
};

/** Looks up an explanation for a raw backend error code. Unknown codes get safe generic copy. */
export const explainIntakeError = (
  code: string | null | undefined,
): IntakeErrorExplanation => {
  const normalized = normalizeCode(code);
  if (normalized === null) return UNKNOWN_INTAKE_ERROR;
  return INTAKE_ERRORS[normalized] ?? UNKNOWN_INTAKE_ERROR;
};

/** True when this code has explicit copy, rather than falling through to the generic explanation. */
export const hasIntakeErrorExplanation = (code: string | null | undefined): boolean => {
  const normalized = normalizeCode(code);
  return normalized !== null && Object.prototype.hasOwnProperty.call(INTAKE_ERRORS, normalized);
};

/**
 * Resolves the explanation for one batch item.
 *
 * `recoverableSecurityHold` is the backend's durable "our infrastructure failed" signal
 * (LeadIdentity/LeadIdentityContracts.cs — BatchReconciliationItemDto). It is authoritative and
 * outranks the code, because `SecurityHoldRecovery` also inspects the recorded scanner signature:
 * a `document_quarantined` occurrence that carries a real signature reached a genuine verdict and
 * is NOT replayable, even though the bare code appears in the recoverable list.
 */
export const explainIntakeItem = (item: {
  errorCode?: string | null;
  intakeStatus?: string | null;
  recoverableSecurityHold?: boolean;
}): IntakeErrorExplanation => {
  const explanation = explainIntakeError(item.errorCode);

  if (item.recoverableSecurityHold === true) {
    // Guarantee infrastructure framing even for a code we have no copy for.
    return explanation.category === 'infrastructure'
      ? explanation
      : { ...INTAKE_ERRORS.security_scanner_unavailable };
  }

  if (item.recoverableSecurityHold === false && explanation.category === 'infrastructure') {
    // The scanner did reach a verdict on this file; replaying it would change nothing.
    return {
      ...explanation,
      title: 'Rejected — this file did not pass inspection',
      whatHappened:
        'Inspection completed for this file and it was not accepted. The original is stored safely and unchanged.',
      nextAction:
        'Re-exporting the document and uploading it again is the only way forward. Contact support if it keeps failing.',
      category: 'content',
      isRetryable: false,
    };
  }

  return explanation;
};

/** True when this item is a replayable infrastructure hold rather than a content rejection. */
export const isInfrastructureHold = (item: {
  errorCode?: string | null;
  intakeStatus?: string | null;
  recoverableSecurityHold?: boolean;
}): boolean => {
  if (item.recoverableSecurityHold === true) return true;
  if (item.recoverableSecurityHold === false) return false;
  // Older payloads predate the durable flag: fall back to the code plus the awaiting status, which
  // is what the backend read model itself keys off.
  if (item.intakeStatus === 'AwaitingSecurityScan') return true;
  return isRecoverableIntakeErrorCode(item.errorCode);
};
