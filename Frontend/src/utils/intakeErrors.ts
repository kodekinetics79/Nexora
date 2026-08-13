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
 *     Backend/ERP_RFQ_Automation/Security/DocumentInspection/DocumentFileInspectionService.cs
 *     (set together with `IsRetryable = true`), and re-projected onto every recoverable hold in
 *     LeadIdentity/LeadIdentityApplicationService.cs.
 *   document_quarantined / document_rejected
 *     Security/DocumentInspection/DocumentInspectionContracts.cs — `DocumentInspectionErrorCodes`,
 *     the default ErrorCode for the Quarantined and Rejected inspection statuses respectively.
 *   macro_enabled_document
 *     Security/DocumentInspection/DocumentInspectionContracts.cs — `DocumentInspectionErrorCodes`,
 *     attached by DocumentFileInspectionService to every macro rejection (legacy OLE VBA storages
 *     and OOXML macroEnabled/vbaProject.bin alike).
 *   malware_detected
 *     Security/DocumentInspection/DocumentFileInspectionService.cs.
 *   unsupported_format
 *     Extraction/ExtractionWorker.cs, and bucketed alongside document_rejected as
 *     UNSUPPORTED_FORMAT by Migrations/20260729221109_PreSecurityDuplicateOccurrenceAccounting.cs.
 *   security_scan_cleared
 *     Security/DocumentInspection/DocumentFileInspectionService.cs.
 *
 * The retryable/terminal split mirrors Extraction/SecurityHoldRecovery.cs:18-22, which is the single
 * definition both the reconciliation read model and the recovery endpoint agree on. Keep this list
 * identical to `SecurityHoldRecovery.RecoverableErrorCodes` — when the two disagreed, the UI counted
 * files as recoverable while the recovery call reported `Eligible: 0` and silently did nothing.
 *
 * ────────────────────────────────────────────────────────────────────────────────────────────────
 * THE SERVER'S REASON OUTRANKS OUR GUESS.
 *
 * This map used to answer with a single static sentence per code, and `document_rejected` is a
 * BUCKET: one code covering macro-blocked, structure-mismatch, unreadable-signature, corrupt
 * allocation table, oversize and empty. Answering all of them with "its contents do not match a
 * document type we can process, or the file is damaged — re-export it, or send it as a PDF" was a
 * guess, and for a macro-enabled workbook every clause of it was wrong: the file is not damaged,
 * re-exporting changes nothing, and a PDF loses the line items.
 *
 * The backend has always computed the precise reason and always persisted it
 * (DocumentIngestionService writes it into `source_document_occurrences.last_error_details`, and
 * both `BatchReconciliationItemDto.reasons` and the governed-upload row's `reason` carry it back).
 * So: when the server supplies a reason, we show the server's reason. The static copy below is the
 * FALLBACK for when it supplies nothing. Entries marked `serverReasonWins: false` are the ones
 * where the code alone already pins the cause and our copy says strictly more than the server's
 * one-liner ("Malware was detected.") — there, the static sentence stays.
 *
 * Server text is never rendered raw: it goes through `presentableServerText` from
 * src/utils/apiErrors.ts — the same bounded-length, no-markup, no-hostname, no-stack-frame,
 * no-raw-object gate every other server string in the product passes through.
 */

import { DOCUMENT_STORAGE_UNAVAILABLE, presentableServerText } from './apiErrors';

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
  /**
   * True when `whatHappened` is OUR inference from the error code alone, because the server sent
   * no usable reason. False when it is the server's own account of what happened.
   *
   * Callers that render supporting detail use this to avoid printing the same sentence twice.
   */
  isInferred: boolean;
}

interface IntakeErrorEntry {
  title: string;
  whatHappened: string;
  nextAction: string;
  category: IntakeFailureCategory;
  isRetryable: boolean;
  /**
   * True when this code is a BUCKET and `whatHappened` can only guess at the cause, so a reason
   * from the server must replace it. False when the code itself is the cause and our copy carries
   * more than the server's one-line reason does.
   */
  serverReasonWins: boolean;
}

/**
 * Mirror of `SecurityHoldRecovery.RecoverableErrorCodes`
 * (Backend/ERP_RFQ_Automation/Extraction/SecurityHoldRecovery.cs:18-22).
 */
export const RECOVERABLE_INTAKE_ERROR_CODES: readonly string[] = [
  'document_quarantined',
  'security_scanner_unavailable',
];

const INTAKE_ERRORS: Record<string, IntakeErrorEntry> = {
  security_scanner_unavailable: {
    title: 'Held — malware scanning is offline',
    whatHappened:
      'Our malware scanner did not respond, so this file was never given a verdict. Nothing is wrong with your document, and the original is stored exactly as you sent it.',
    nextAction:
      'No re-upload is needed. This file processes automatically when scanning recovers, or you can retry it now.',
    category: 'infrastructure',
    isRetryable: true,
    // The code IS the cause, and the reassurance here is the part the user needs; the scanner's own
    // one-liner ("the scan timed out") does not carry it.
    serverReasonWins: false,
  },
  document_quarantined: {
    title: 'Held pending a security verdict',
    whatHappened:
      'This file was set aside before inspection finished, so it has no scan result yet. The original is stored safely and unchanged.',
    nextAction:
      'No re-upload is needed. Retry it once scanning is available, or leave it to process automatically.',
    category: 'infrastructure',
    isRetryable: true,
    // A status, not a cause: several different verdicts land here, so a recorded reason wins.
    serverReasonWins: true,
  },
  malware_detected: {
    title: 'Blocked — malware detected',
    whatHappened:
      'Our scanner identified malicious content in this file, so it was blocked and never processed.',
    nextAction:
      'Do not re-upload this file. Check the source with your IT team and request a clean copy from the sender.',
    category: 'content',
    isRetryable: false,
    serverReasonWins: false,
  },
  macro_enabled_document: {
    title: 'Blocked — this file contains macros',
    whatHappened:
      'This file contains macros (embedded VBA code), which Nexora does not open — macro-enabled Office files are a common way malware is delivered. Save a macro-free copy (.xlsx in Excel, .docx in Word) or ask the sender for one.',
    nextAction:
      'Upload the macro-free copy instead. The version with macros will always be blocked.',
    category: 'content',
    isRetryable: false,
    // The backend's sentence names the format precisely ("this workbook" / "this document") and the
    // exact Save As target, so prefer it; the copy above is the fallback when nothing came through.
    serverReasonWins: true,
  },
  document_rejected: {
    title: 'Rejected — this file did not pass inspection',
    whatHappened:
      'Inspection did not accept this file, and no specific reason reached this screen. The original is stored safely and unchanged.',
    nextAction:
      'Check that the document opens correctly, re-save it, and upload it again. Contact support with the batch reference if it stops again.',
    category: 'content',
    isRetryable: false,
    // The bucket code. Whatever the server said about THIS file is the truth; the copy above is
    // only what we can honestly say when it said nothing.
    serverReasonWins: true,
  },
  unsupported_format: {
    title: 'Rejected — unsupported format',
    whatHappened:
      'This file type is not one Nexora can extract commercial facts from.',
    nextAction:
      'Convert the document to PDF, Word, Excel, CSV or an image, then upload it again.',
    category: 'content',
    isRetryable: false,
    serverReasonWins: true,
  },
  security_scan_cleared: {
    title: 'Security scan passed',
    whatHappened: 'This file was scanned and no malicious content was found.',
    nextAction: 'No action is needed.',
    category: 'cleared',
    isRetryable: false,
    serverReasonWins: false,
  },
  // Backend/ERP_RFQ_Automation/Infrastructure/Storage/IEvidenceObjectStorage.cs —
  // EvidenceStorageUnavailableException.ErrorCode, the same string SecurityScanRecoveryService
  // already emits for the read side.
  //
  // Nexora stores every source immutably before it queues anything, so when the store refuses a
  // write the document was not accepted — and, if the store is misconfigured, it will refuse the
  // next attempt for the same reason. On 2026-08-12 this fault wore `ingestion_failed`'s copy
  // instead, and four .doc files were each answered with "upload this file again" while the
  // readiness probe already knew the bucket did not exist.
  //
  // `whatHappened` says nothing about the rest of the batch: a store can fail PART WAY through
  // one, and only the caller knows how much it accepted first. Claiming "nothing was accepted"
  // here would have the banner deny work that is already running.
  //
  // `nextAction` is the neutral fallback for a bare code. When the caller knows which fault it is,
  // `explainStoragePause` replaces it — the two faults need opposite instructions.
  //
  // Absent from RECOVERABLE_INTAKE_ERROR_CODES deliberately: the security sweep replays stored
  // sources, and here there is no stored source to replay.
  evidence_storage_unavailable: {
    title: 'Uploads are paused — document storage is unavailable',
    whatHappened:
      'Nexora stores every document before it starts processing, and document storage cannot be written right now. Anything it could not store was refused outright rather than half-accepted.',
    nextAction:
      'The fault is in Nexora document storage, not in your documents. Your administrator can see its cause on the service health page.',
    category: 'infrastructure',
    isRetryable: false,
    // The code IS the cause. The server's companion sentence is one clause of the copy above.
    serverReasonWins: false,
  },
  // Controllers/ExtractionController.cs — the ingest boundary's poison-file isolation, so one bad
  // file never fails the whole batch. Not a scanner hold, so the recovery sweep cannot replay it:
  // the way forward is uploading the file again.
  ingestion_failed: {
    title: 'This file could not be accepted',
    whatHappened:
      'Something went wrong while queueing this file, so it never started processing. The rest of the batch was unaffected.',
    nextAction: 'Upload this file again. Contact support with the batch reference if it fails a second time.',
    category: 'content',
    isRetryable: false,
    // The server's companion string here is "Failed to enqueue file." — strictly less than this.
    serverReasonWins: false,
  },
};

const UNKNOWN_INTAKE_ERROR: IntakeErrorEntry = {
  title: 'This file needs attention',
  whatHappened:
    'Processing stopped for this file before it produced a result. The original document is stored safely and unchanged.',
  nextAction:
    'Retry it, or contact support with the batch reference if it stops again.',
  category: 'content',
  isRetryable: false,
  serverReasonWins: true,
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

/**
 * Turns one entry plus whatever the server said into the explanation a page renders.
 *
 * The server's reason replaces `whatHappened` only when the entry admits it is a guess AND the
 * reason survives the shared presentability gate. `nextAction` always stays ours: a remedy is a
 * product decision, and the reason field is not required to contain one.
 */
const resolve = (
  entry: IntakeErrorEntry,
  serverReason?: string | null,
): IntakeErrorExplanation => {
  const { serverReasonWins, ...copy } = entry;
  const reason = serverReasonWins ? presentableServerText(serverReason) : null;
  return reason === null
    ? { ...copy, isInferred: true }
    : { ...copy, whatHappened: reason, isInferred: false };
};

/**
 * Looks up an explanation for a raw backend error code. Unknown codes get safe generic copy.
 *
 * @param serverReason The reason the backend recorded for THIS file — `job.reason` on a governed
 *        upload row, or `item.reasons[0]` on a batch reconciliation row. When present and safe to
 *        render it replaces our inferred sentence, because it is the only account of what actually
 *        happened to this particular document.
 */
export const explainIntakeError = (
  code: string | null | undefined,
  serverReason?: string | null,
): IntakeErrorExplanation => {
  const normalized = normalizeCode(code);
  const entry = normalized === null
    ? UNKNOWN_INTAKE_ERROR
    : INTAKE_ERRORS[normalized] ?? UNKNOWN_INTAKE_ERROR;
  return resolve(entry, serverReason);
};

/**
 * The storage pause, told the way the fault actually behaves.
 *
 * A misspelled bucket and a provider that blinked produce the same refusal but opposite remedies,
 * and the static entry above can only pick one. It picked "do not upload these files again", which
 * is right for a typo and wrong for a thirty-second outage — dressing a blip as something only an
 * administrator can fix is the 2026-08-12 defect inverted, and just as expensive to the person
 * holding the documents.
 *
 * `isRetryable` follows the same split, so a caller can decide whether to offer the action at all.
 */
export const explainStoragePause = (isConfigurationFault: boolean): IntakeErrorExplanation => ({
  ...resolve(INTAKE_ERRORS[DOCUMENT_STORAGE_UNAVAILABLE]),
  nextAction: isConfigurationFault
    ? 'Document storage is not configured correctly. Waiting will not clear this — an administrator has to correct the storage settings, and uploading these files again before then will be refused the same way.'
    : 'Document storage is not responding. This can clear on its own — try again shortly, and tell your administrator if it persists.',
  isRetryable: !isConfigurationFault,
});

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
 *
 * `reasons[0]` is the reason the backend recorded for this occurrence
 * (LeadIdentityApplicationService.IntakeReasons reads it straight out of
 * `last_error_details->>'reason'`), so it is passed through as the server reason.
 */
export const explainIntakeItem = (item: {
  errorCode?: string | null;
  intakeStatus?: string | null;
  recoverableSecurityHold?: boolean;
  reasons?: string[] | null;
}): IntakeErrorExplanation => {
  const serverReason = Array.isArray(item.reasons)
    ? item.reasons.find((reason) => presentableServerText(reason) !== null) ?? null
    : null;
  const explanation = explainIntakeError(item.errorCode, serverReason);

  if (item.recoverableSecurityHold === true) {
    // Guarantee infrastructure framing even for a code we have no copy for.
    return explanation.category === 'infrastructure'
      ? explanation
      : resolve(INTAKE_ERRORS.security_scanner_unavailable);
  }

  if (item.recoverableSecurityHold === false && explanation.category === 'infrastructure') {
    // The scanner did reach a verdict on this file; replaying it would change nothing. What the
    // verdict WAS is not something this branch knows, so it must not invent one — the recorded
    // reason is used when there is one, and the wording stays non-committal when there is not.
    return resolve(
      {
        title: 'Rejected — this file did not pass inspection',
        whatHappened:
          'Inspection completed for this file and it was not accepted. The original is stored safely and unchanged.',
        nextAction:
          'Check that the document opens correctly, re-save it, and upload it again. Contact support with the batch reference if it keeps failing.',
        category: 'content',
        isRetryable: false,
        serverReasonWins: true,
      },
      serverReason,
    );
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
