import axiosInstance from '../axiosInstance';

/**
 * Inbound-mail triage.
 *
 * Every message that reaches the ingestion mailbox is now given a decision BEFORE any AI is spent
 * on it: it is either treated as a customer inquiry, routed as a supplier/commercial document,
 * rejected as noise (auto-replies, mailing lists, bounces, calendar invites), or left uncertain and
 * extracted anyway. This module is the read/repair surface for those decisions.
 *
 * The product reason this exists: a rejected email is a deal the system decided not to look at. If
 * that decision is invisible, a missed cable-tray enquiry is indistinguishable from an empty inbox.
 * So the rejection list is a first-class screen and every row carries a way back in.
 *
 * Contract this file depends on (backend §1.11):
 *   GET  /api/email-triage?outcome=&page=   → id, receivedOn, from, subject, outcome,
 *                                             reasonCodes, hasAttachments, linkedBatchId
 *   POST /api/email-triage/{id}/reprocess   → { reason, idempotencyKey }, RBAC "Leads"
 *
 * Everything ELSE this module reads is optional and defaults to "not reported". The backend lock
 * ships separately; a field that is not there yet must degrade to an honest absence, never to a
 * zero, a blank, or a claim the UI cannot support.
 */

/** The four decisions the deterministic triage can reach. Unknown strings are preserved verbatim. */
export type EmailTriageOutcome = 'Inquiry' | 'CommercialNonInquiry' | 'Noise' | 'Uncertain';

export const TRIAGE_OUTCOMES: EmailTriageOutcome[] = [
  'Noise',
  'CommercialNonInquiry',
  'Uncertain',
  'Inquiry',
];

/**
 * Reason codes are stable snake_case identifiers emitted by the backend triage. This map is OWNED
 * BY THE FRONTEND on purpose — no import from src/utils/intakeErrors.ts — so triage wording can
 * change without touching the document-intake error vocabulary.
 */
export const TRIAGE_REASON_LABELS: Record<string, string> = {
  auto_submitted_header: 'Auto-submitted',
  bulk_list_header: 'Bulk / mailing list',
  noreply_sender: 'No-reply sender',
  empty_after_quote_strip: 'Nothing new in the message',
  calendar_invite: 'Calendar invite',
  known_customer_contact: 'Known customer contact',
  qty_uom_pattern: 'Quantity and unit',
  rfq_reference: 'RFQ wording',
  request_verb: 'Asked us to quote',
  supplier_quote_terms: 'Supplier quotation wording',
  invoice_terms: 'Invoice wording',
  po_terms: 'Purchase-order wording',
  no_signal: 'No decisive signal',
};

/** The sentence shown under the chip — why this evidence pushed the decision where it went. */
export const TRIAGE_REASON_DESCRIPTIONS: Record<string, string> = {
  auto_submitted_header:
    'The message carried an Auto-Submitted header, which senders set on out-of-office and machine-generated replies.',
  bulk_list_header:
    'The message carried mailing-list or bulk-precedence headers, so it was addressed to a list rather than to us.',
  noreply_sender: 'The sender address is a no-reply, bounce or postmaster mailbox that cannot be answered.',
  empty_after_quote_strip:
    'Once the quoted thread and signature were removed there was no new text, and no attachment came with it.',
  calendar_invite: 'The message is a calendar invitation, not correspondence.',
  known_customer_contact: 'The sender is already recorded as a contact at one of your customers.',
  qty_uom_pattern:
    'The body states a quantity next to a unit of measure — for example "40 nos" or "12 sets" — which is how a prose enquiry asks for goods.',
  rfq_reference: 'The subject line names an RFQ, enquiry, tender or invitation to bid.',
  request_verb: 'The body directly asks us to quote, send, advise or offer.',
  supplier_quote_terms: 'The sender is a supplier and the message uses quotation wording such as validity or unit price.',
  invoice_terms: 'The sender is a supplier and the message uses invoice wording.',
  po_terms: 'The message uses purchase-order wording.',
  no_signal:
    'Nothing in the message pointed either way. It was still extracted — an uncertain message is never dropped.',
};

/** Humanises a reason code the deployment emitted but this build does not know about yet. */
export const describeTriageReason = (code: string): string =>
  TRIAGE_REASON_LABELS[code] ??
  code
    .replaceAll('_', ' ')
    .replace(/(^|\s)\S/g, (letter) => letter.toUpperCase());

export interface TriageOutcomeCopy {
  /** Column value and tab label. */
  label: string;
  /** One sentence stating what the system DID, in the past tense. */
  meaning: string;
  chipColor: 'default' | 'error' | 'info' | 'warning' | 'success';
}

export const TRIAGE_OUTCOME_COPY: Record<string, TriageOutcomeCopy> = {
  Noise: {
    label: 'Rejected as noise',
    meaning: 'No lead was attempted and no AI was spent. The original email is still stored.',
    chipColor: 'error',
  },
  CommercialNonInquiry: {
    label: 'Routed as supplier document',
    meaning: 'Treated as a supplier quotation or invoice, so it was not turned into a customer inquiry.',
    chipColor: 'info',
  },
  Uncertain: {
    label: 'Uncertain — extracted anyway',
    meaning: 'Nothing decisive was found, so the message was extracted and flagged for a human.',
    chipColor: 'warning',
  },
  Inquiry: {
    label: 'Extracted as inquiry',
    meaning: 'Recognised as a customer enquiry and sent for extraction.',
    chipColor: 'success',
  },
};

export const describeTriageOutcome = (outcome: string): TriageOutcomeCopy =>
  TRIAGE_OUTCOME_COPY[outcome] ?? {
    label: outcome,
    meaning: 'This deployment reported a decision this screen does not recognise yet.',
    chipColor: 'default',
  };

/**
 * One triaged message.
 *
 * `id` and `outcome` are the only fields guaranteed to be usable. Everything typed `| null` may be
 * absent from the payload and MUST render as an explicit absence.
 */
export interface EmailTriageRow {
  id: number;
  receivedOn: string | null;
  from: string | null;
  subject: string | null;
  /** Raw server value; compare through {@link describeTriageOutcome}, never by colour alone. */
  outcome: string;
  reasonCodes: string[];
  /** Null when the deployment does not report it — distinct from `false`. */
  hasAttachments: boolean | null;
  linkedBatchId: string | null;

  // ---- Optional enrichment. Absent on a deployment that has not shipped it. ----
  /**
   * The fresh message text — the part left after the quoted thread and signature were stripped.
   * For a conversational enquiry this prose IS the evidence, so it is shown beside the extraction.
   */
  bodyPreview: string | null;
  /** True when the body itself was submitted for extraction (as opposed to attachments only). */
  bodySubmitted: boolean | null;
  attachmentCount: number | null;
  attachmentNames: string[];
  /** Reported only when the payload actually carried an attachment-name array. */
  attachmentNamesReported: boolean;
  /** SUPPLIER_QUOTE / SUPPLIER_INVOICE — set when the message was routed rather than extracted. */
  commercialDocumentTypeHint: string | null;
  /** True when the message continues an existing thread (In-Reply-To / References present). */
  threadContinuation: boolean | null;
  /** The lead this message produced, when one exists. */
  leadId: number | null;
  /** How many line items came out of the body. */
  extractedItemCount: number | null;
  decidedOn: string | null;
}

export interface EmailTriagePage {
  items: EmailTriageRow[];
  /** Null when the deployment returns a bare array with no total. */
  totalCount: number | null;
  pageNumber: number;
  pageSize: number | null;
}

export interface ListTriageParams {
  /** Omit for "every decision". */
  outcome?: EmailTriageOutcome | string;
  page?: number;
  pageSize?: number;
}

export interface ReprocessTriageResult {
  id: number | null;
  /** Server-reported status, e.g. Queued. Null when the endpoint answers with an empty body. */
  status: string | null;
  batchId: string | null;
  /** True when the idempotency key matched an earlier call and nothing new was queued. */
  replayed: boolean | null;
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null;

const asText = (value: unknown): string | null => {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
};

/** Keeps internal newlines (the message body is read as prose) while dropping blank payloads. */
const asProse = (value: unknown): string | null => {
  if (typeof value !== 'string') return null;
  return value.trim().length > 0 ? value : null;
};

const asCount = (value: unknown): number | null =>
  typeof value === 'number' && Number.isFinite(value) ? value : null;

const asFlag = (value: unknown): boolean | null => (typeof value === 'boolean' ? value : null);

const asTextList = (value: unknown): string[] =>
  Array.isArray(value) ? value.map(asText).filter((entry): entry is string => entry !== null) : [];

/**
 * Reads one row defensively. Alternate key spellings are accepted because the list endpoint and any
 * future detail endpoint should not be able to break this screen by disagreeing on a name.
 */
export const readTriageRow = (payload: unknown): EmailTriageRow => {
  const root = isRecord(payload) ? payload : {};
  const attachmentNamesRaw = Array.isArray(root.attachmentNames)
    ? root.attachmentNames
    : Array.isArray(root.attachmentFileNames)
      ? root.attachmentFileNames
      : null;
  const attachmentNames = asTextList(attachmentNamesRaw);
  const attachmentCount = asCount(root.attachmentCount) ?? (attachmentNamesRaw ? attachmentNames.length : null);

  return {
    id: asCount(root.id) ?? asCount(root.emailIngestId) ?? 0,
    receivedOn: asText(root.receivedOn) ?? asText(root.receivedDate),
    from: asText(root.from) ?? asText(root.fromAddress) ?? asText(root.sender),
    subject: asText(root.subject),
    outcome: asText(root.outcome) ?? asText(root.triageOutcome) ?? 'Unknown',
    reasonCodes: asTextList(root.reasonCodes ?? root.triageReasonCodes),
    hasAttachments: asFlag(root.hasAttachments) ?? (attachmentCount === null ? null : attachmentCount > 0),
    linkedBatchId: asText(root.linkedBatchId) ?? asText(root.batchId),

    bodyPreview:
      asProse(root.bodyPreview) ?? asProse(root.freshBody) ?? asProse(root.bodyText) ?? asProse(root.bodySnippet),
    bodySubmitted: asFlag(root.bodySubmitted) ?? asFlag(root.bodyEnqueued),
    attachmentCount,
    attachmentNames,
    attachmentNamesReported: attachmentNamesRaw !== null,
    commercialDocumentTypeHint: asText(root.commercialDocumentTypeHint) ?? asText(root.documentTypeHint),
    threadContinuation: asFlag(root.threadContinuation),
    leadId: asCount(root.leadId),
    extractedItemCount: asCount(root.extractedItemCount) ?? asCount(root.itemCount),
    decidedOn: asText(root.decidedOn) ?? asText(root.triageDecidedOn),
  };
};

/** Accepts both the paginated envelope and a bare array, so either backend shape renders. */
export const readTriagePage = (payload: unknown, requestedPage: number): EmailTriagePage => {
  if (Array.isArray(payload)) {
    const items = payload.map(readTriageRow);
    return { items, totalCount: items.length, pageNumber: requestedPage, pageSize: null };
  }
  const root = isRecord(payload) ? payload : {};
  const rawItems = Array.isArray(root.items) ? root.items : Array.isArray(root.rows) ? root.rows : [];
  return {
    items: rawItems.map(readTriageRow),
    totalCount: asCount(root.totalCount) ?? asCount(root.total),
    pageNumber: asCount(root.pageNumber) ?? requestedPage,
    pageSize: asCount(root.pageSize),
  };
};

const readReprocessResult = (payload: unknown): ReprocessTriageResult => {
  const root = isRecord(payload) ? payload : {};
  return {
    id: asCount(root.id) ?? asCount(root.emailIngestId),
    status: asText(root.status) ?? asText(root.parseStatus),
    batchId: asText(root.batchId) ?? asText(root.linkedBatchId),
    replayed: asFlag(root.replayed) ?? asFlag(root.idempotentReplay),
  };
};

/**
 * True when the deployment simply does not expose triage yet (backend lock not shipped). Rendered
 * as an explanation rather than as a failure — nothing is broken, the feature is just not there.
 */
export const isTriageUnavailable = (error: unknown): boolean => {
  if (!isRecord(error)) return false;
  const response = isRecord(error.response) ? error.response : undefined;
  const status = typeof response?.status === 'number' ? response.status : undefined;
  return status === 404 || status === 501;
};

const emailTriageService = {
  listTriage: async (params: ListTriageParams = {}): Promise<EmailTriagePage> => {
    const page = params.page && params.page > 0 ? params.page : 1;
    const query: Record<string, string | number> = { page };
    if (params.outcome) query.outcome = params.outcome;
    if (params.pageSize) query.pageSize = params.pageSize;
    const response = await axiosInstance.get('/api/email-triage', { params: query });
    return readTriagePage(response.data, page);
  },

  /**
   * Puts a triaged message back through ingestion as if it were uncertain — the human override for
   * "this WAS an inquiry". The reason is mandatory: it is the audit record for overturning a
   * machine decision, and the idempotency key stops a double-click from queueing the message twice.
   */
  reprocess: async (
    id: number,
    reason: string,
    idempotencyKey: string = crypto.randomUUID(),
  ): Promise<ReprocessTriageResult> => {
    const response = await axiosInstance.post(
      `/api/email-triage/${id}/reprocess`,
      { reason, idempotencyKey },
      { headers: { 'Idempotency-Key': idempotencyKey } },
    );
    return readReprocessResult(response.data);
  },
};

export default emailTriageService;
