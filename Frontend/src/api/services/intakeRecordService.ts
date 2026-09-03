import axiosInstance from '../axiosInstance';

/**
 * The one queryable record per processed email — `GET /api/intake-records/...`.
 *
 * The endpoint (`IntakeRecordsController`) has existed since the canonical-record work landed and
 * had ZERO frontend callers, so the ordinary question "what did we actually receive?" had no
 * answer anywhere in the product: a rep looking at a lead could see the fields the pipeline
 * extracted, but not which files arrived, which were dropped at the door, or why.
 *
 * Only the parts a rep reads are typed here. The record also carries per-field evidence,
 * validation findings and the identity arbiter's decision, which have their own screens.
 */

/** Files that arrived on the message, each with what became of it. */
export interface IntakeInventoryEntry {
  /** "Attachment" | "Body" — the extracted message body is listed as a row of its own. */
  kind: string;
  /** "Enqueued" — it entered extraction | "Skipped" — the intake door dropped it. */
  disposition: string;
  fileName: string;
  jobStatus?: string | null;
  jobLastError?: string | null;
  resultLeadId?: number | null;
  /** Present only on a skipped file: the door's own reason. */
  skippedReason?: string | null;
  securityStatus?: string | null;
}

export interface IntakeRecord {
  sourceEmail: {
    emailIngestId: number;
    mailbox: string;
    messageId: string;
    receivedOn: string;
    /** True only when the stored .eml provably exists right now — never inferred from a path. */
    rawEmailAvailable: boolean;
    parseStatus?: string | null;
  };
  classification: {
    triageOutcome: string;
    triageReasonCodes: string[];
    processingPath?: string | null;
    externalAiUsed?: boolean | null;
  };
  message: { from?: string | null; to?: string | null; subject?: string | null; sentOn?: string | null };
  inventory: IntakeInventoryEntry[];
  /** One email can split into several leads; these are the siblings of the one being viewed. */
  otherLeadIds: number[];
  finalStatus: string;
}

const intakeRecordService = {
  /**
   * The record behind ONE lead. Requires the Email Intake entitlement, so a tenant without it
   * gets a 403 — which is a fact about the plan, not a failure, and callers must present it as one.
   */
  getByLead: async (leadId: number): Promise<IntakeRecord> =>
    (await axiosInstance.get<IntakeRecord>(`/api/intake-records/by-lead/${leadId}`)).data,
};

export default intakeRecordService;
