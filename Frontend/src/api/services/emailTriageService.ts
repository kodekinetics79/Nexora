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
 *   POST /api/Email/fetch                   → a manual mailbox poll, RBAC "Leads:Create"
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
 * Server state tokens are not spelled consistently across the pipeline — `NeedsReview`,
 * `needs_review` and `NEEDS-REVIEW` all occur. Matching on a stripped, lowercased token means a
 * spelling change cannot silently demote a known state to "unrecognised", which is the failure
 * that would put a message in front of nobody.
 */
const stateToken = (value: string): string => value.replace(/[^a-z0-9]/gi, '').toLowerCase();

/** Title-cases a machine token for a LABEL. Never used on a reason — see the page's reason gate. */
const humanise = (value: string): string =>
  value
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replaceAll('_', ' ')
    .replaceAll('-', ' ')
    .toLowerCase()
    .replace(/(^|\s)\S/g, (letter) => letter.toUpperCase());

export interface AssemblyStateCopy {
  label: string;
  /** One sentence stating where the message actually is, in plain words. */
  meaning: string;
  chipColor: 'default' | 'error' | 'info' | 'warning' | 'success';
  /** The worker has not finished; this row is not a final answer and will change on its own. */
  inProgress: boolean;
  /** No lead will ever appear here unless a person acts. */
  needsHuman: boolean;
  /** False when the deployment reported a state this build has no wording for. */
  recognised: boolean;
}

/**
 * What "assembly" means: one inbound message can carry a body and several attachments, and all of
 * them have to land before a single Lead can be made. Until they do, the message is neither a
 * success nor a failure — it is waiting, and saying so is the whole point of this column.
 */
const ASSEMBLY_STATE_COPY: Record<string, AssemblyStateCopy> = {
  captured: {
    label: 'Stored — not read yet',
    meaning:
      'The message and its original are durably recorded. Nothing has been inspected yet, so no lead exists.',
    chipColor: 'default',
    inProgress: true,
    needsHuman: false,
    recognised: true,
  },
  inspecting: {
    label: 'Being scanned',
    meaning: 'The parts of this message are being scanned and checked. No lead exists yet.',
    chipColor: 'info',
    inProgress: true,
    needsHuman: false,
    recognised: true,
  },
  extracting: {
    label: 'Reading',
    meaning: 'The parts of this message are being read for line items. No lead exists yet.',
    chipColor: 'info',
    inProgress: true,
    needsHuman: false,
    recognised: true,
  },
  readyforassembly: {
    label: 'Ready to assemble',
    meaning: 'Every part is finished and at least one carried commercial content. The lead is next.',
    chipColor: 'info',
    inProgress: true,
    needsHuman: false,
    recognised: true,
  },
  assembled: {
    label: 'Inquiry created',
    meaning: 'Every part of this message was merged into one lead — one inquiry, not one per file.',
    chipColor: 'success',
    inProgress: false,
    needsHuman: false,
    recognised: true,
  },
  needsreview: {
    label: 'Needs review',
    meaning:
      'A person has to look at this message: the grouping was ambiguous, confidence was low, or a part '
      + 'could not be read. Terminal for the pipeline, open for you.',
    chipColor: 'warning',
    inProgress: false,
    needsHuman: true,
    recognised: true,
  },
  failedrecoverable: {
    // The hold-reason vocabulary is explicit that nothing sweeps held components in this build, so
    // this must not promise an automatic retry — it says what is true and names the human action.
    label: 'Held — service unavailable',
    meaning:
      'Something this system depends on was unavailable. It is not the sender’s fault and nothing was '
      + 'lost, but nothing picks this up on its own — reprocess the message to run it again.',
    chipColor: 'warning',
    inProgress: false,
    needsHuman: true,
    recognised: true,
  },
  rejectedsecurity: {
    label: 'Rejected — security',
    meaning:
      'A part of this message was refused on security grounds, so it cannot proceed and no lead was '
      + 'created. The original is retained for the security record.',
    chipColor: 'error',
    inProgress: false,
    needsHuman: true,
    recognised: true,
  },
  noinquiry: {
    // Not a defect and not a loss: distinguishing it from NeedsReview is the whole reason the state
    // exists, so it must not be coloured like a failure.
    label: 'Nothing to quote',
    meaning:
      'The message carried no commercial content at all — no fresh text and no parts — so no lead was '
      + 'created. The original is retained.',
    chipColor: 'default',
    inProgress: false,
    needsHuman: false,
    recognised: true,
  },
  rejected: {
    label: 'Rejected',
    meaning: 'This message was rejected during assembly, so no lead was created. The original is retained.',
    chipColor: 'error',
    inProgress: false,
    needsHuman: true,
    recognised: true,
  },
};

/**
 * Spellings this build accepts for the same state. These are drift insurance, not the contract:
 * the canonical names above are the `EmailInquiryAssemblyStatus` members.
 */
const ASSEMBLY_STATE_ALIASES: Record<string, string> = {
  pending: 'captured',
  queued: 'captured',
  notstarted: 'captured',
  scanning: 'inspecting',
  inprogress: 'inspecting',
  processing: 'extracting',
  reading: 'extracting',
  ready: 'readyforassembly',
  completed: 'assembled',
  complete: 'assembled',
  succeeded: 'assembled',
  done: 'assembled',
  review: 'needsreview',
  awaitingreview: 'needsreview',
  heldforreview: 'needsreview',
  failed: 'failedrecoverable',
  faulted: 'failedrecoverable',
  held: 'failedrecoverable',
  discarded: 'rejected',
  nocommercialcontent: 'noinquiry',
};

/**
 * An unknown state is reported verbatim and treated as neither finished nor in flight. It must
 * never be assumed successful: the "Open lead" action is gated on an actual lead id, so a state
 * this build cannot read degrades to "we do not know", not to a dead link.
 */
export const describeAssemblyState = (state: string | null): AssemblyStateCopy => {
  if (state === null) {
    return {
      label: 'Not reported',
      meaning: 'This deployment does not report assembly state yet.',
      chipColor: 'default',
      inProgress: false,
      needsHuman: false,
      recognised: false,
    };
  }
  const token = stateToken(state);
  const copy = ASSEMBLY_STATE_COPY[token] ?? ASSEMBLY_STATE_COPY[ASSEMBLY_STATE_ALIASES[token] ?? ''];
  return (
    copy ?? {
      label: humanise(state),
      meaning: 'This deployment reported an assembly state this screen does not recognise yet.',
      chipColor: 'default',
      inProgress: false,
      needsHuman: false,
      recognised: false,
    }
  );
};

/**
 * WHAT ACTUALLY BECAME OF THIS MESSAGE — the one status a reader should be able to act on.
 *
 * <p><b>Why this exists.</b> The per-ingest checkpoint (`ParseStatus`) and the message-level
 * assembly state disagree, and on live data they disagree in the direction that reads BACKWARDS.
 * Measured on the live tenant: ingests whose assembly is closed at NeedsReview carry
 * `ParseStatus = Queued`, which reads as "still being worked on" for a message that is finished
 * and produced nothing; and ingests that DID produce a lead carry `ParseStatus = NeedsReview`,
 * which reads as a problem for what is actually a success waiting for approval. A reader
 * scanning that column would walk past every loss and investigate every win.</p>
 *
 * <p>The checkpoint is not wrong and is not changed — `LeadPersister` writes NeedsReview
 * deliberately because AI-derived figures always need human approval, and the review queue keys
 * on that exact string. It is simply not a description of the MESSAGE. So the screen derives its
 * status from the assembly, plus the one fact that separates a success from a loss: whether a
 * lead exists. Where the two disagree, the assembly wins.</p>
 *
 * <p>Three answers, and a reader must be able to tell them apart at a glance:</p>
 * <ul>
 *   <li><b>in-flight</b> — something is still going to move this message.</li>
 *   <li><b>inquiry-created</b> — finished, and it produced an inquiry. A SUCCESS, even when the
 *       inquiry still needs approving.</li>
 *   <li><b>no-inquiry</b> — finished, and it produced nothing. A LOSS, whatever the checkpoint
 *       says.</li>
 * </ul>
 */
export type MessageProgressKind = 'in-flight' | 'inquiry-created' | 'no-inquiry' | 'unknown';

export interface MessageProgressCopy {
  label: string;
  meaning: string;
  chipColor: 'default' | 'error' | 'info' | 'warning' | 'success';
  kind: MessageProgressKind;
}

export const describeMessageProgress = (
  assemblyState: string | null,
  hasLead: boolean,
): MessageProgressCopy => {
  const assembly = describeAssemblyState(assemblyState);

  // A lead is proof of the outcome, and it outranks a state this build cannot read. Mail that
  // predates the assembly aggregate reports no state at all and is judged on the lead alone.
  if (assemblyState === null || !assembly.recognised) {
    if (hasLead)
      return {
        label: 'Inquiry created',
        meaning: 'This message produced an inquiry. Open it to see what was extracted.',
        chipColor: 'success',
        kind: 'inquiry-created',
      };
    return {
      label: assemblyState === null ? 'Not reported' : assembly.label,
      meaning: assembly.meaning,
      chipColor: 'default',
      kind: 'unknown',
    };
  }

  if (assembly.inProgress)
    return {
      label: assembly.label,
      meaning: assembly.meaning,
      chipColor: assembly.chipColor,
      kind: 'in-flight',
    };

  // Finished WITH a lead. Including from NeedsReview: a reviewer can pull an assembled message
  // back for attention and the lead it already produced stays. That is a success awaiting a
  // person, not a failure, and colouring it like one is what taught readers to ignore the column.
  if (hasLead)
    return {
      label:
        stateToken(assemblyState) === 'needsreview'
          ? 'Inquiry created — needs your review'
          : 'Inquiry created',
      meaning:
        'This message produced an inquiry. It is finished; anything still outstanding is a '
        + 'decision for you, not work the system is doing.',
      chipColor: 'success',
      kind: 'inquiry-created',
    };

  // Finished WITHOUT a lead. The loss case, and the one the checkpoint dressed up as progress.
  return {
    label: `No inquiry — ${assembly.label.toLowerCase()}`,
    meaning: assembly.meaning,
    chipColor: assembly.chipColor === 'success' ? 'warning' : assembly.chipColor,
    kind: 'no-inquiry',
  };
};

export interface ComponentStateCopy {
  label: string;
  chipColor: 'default' | 'error' | 'info' | 'warning' | 'success';
}

/**
 * `EmailInquiryComponentStatus`, in the words a salesperson reads.
 *
 * `Skipped` is the one that must not look benign: it means the sender attached something this
 * system could not process, which is a quotation priced against a document nobody read if it goes
 * unnoticed. `Ignored` is its opposite — a signature logo — and is deliberately quiet.
 */
const COMPONENT_STATE_COPY: Record<string, ComponentStateCopy> = {
  pending: { label: 'Queued', chipColor: 'default' },
  inspecting: { label: 'Being scanned', chipColor: 'info' },
  extracting: { label: 'Being read', chipColor: 'info' },
  completed: { label: 'Read', chipColor: 'success' },
  skipped: { label: 'Could not be read', chipColor: 'warning' },
  refusedsecurity: { label: 'Refused — security', chipColor: 'error' },
  failedrecoverable: { label: 'Held — service unavailable', chipColor: 'warning' },
  ignored: { label: 'Ignored — not commercial', chipColor: 'default' },
  structuralonly: { label: 'Container — content is in its parts', chipColor: 'default' },
};

const COMPONENT_STATE_ALIASES: Record<string, string> = {
  inprogress: 'extracting',
  processing: 'extracting',
  complete: 'completed',
  done: 'completed',
  failed: 'failedrecoverable',
  held: 'failedrecoverable',
};

export const describeComponentState = (state: string | null): ComponentStateCopy => {
  if (state === null) return { label: 'Not reported', chipColor: 'default' };
  const token = stateToken(state);
  const copy = COMPONENT_STATE_COPY[token] ?? COMPONENT_STATE_COPY[COMPONENT_STATE_ALIASES[token] ?? ''];
  return copy ?? { label: humanise(state), chipColor: 'default' };
};

/** `EmailInquiryComponentKind`. "Body" on its own reads as jargon on a sales screen. */
const COMPONENT_KIND_LABELS: Record<string, string> = {
  body: 'Message text',
  attachment: 'Attachment',
  embeddedmessage: 'Forwarded email',
};

export const describeComponentKind = (kind: string | null): string | null =>
  kind === null ? null : COMPONENT_KIND_LABELS[stateToken(kind)] ?? humanise(kind);

/**
 * One part of a message that has to land before the lead can be made — the body itself, or one
 * attachment. Every field is optional: a deployment that does not report components yet must show
 * an absence, not a row of blanks that reads as a file with no name and no state.
 */
export interface EmailComponent {
  /** Row key: the server id when there is one, otherwise the position within the message. */
  key: string;
  /** The part's position in the message, when reported. */
  ordinal: number | null;
  fileName: string | null;
  /** Body / Attachment / … — the server's own vocabulary, humanised for display only. */
  kind: string | null;
  state: string | null;
  /** Why this part failed, or why it is waiting for a person. Null when neither applies. */
  reason: string | null;
}

/**
 * One attachment the intake door deliberately did NOT hand to extraction — ING-06.
 *
 * It is not a component and never becomes one: no job was queued for it, so it is absent from the
 * assembly totals and from the component list by design. It is still a piece of what the customer
 * sent, and the only record of it is this entry.
 */
export interface SkippedAttachment {
  /** Row key: the entry's position in the recorded list, which is all the backend gives. */
  key: string;
  /** The sender's own file name, reduced to a leaf. Null when the entry carried no usable name. */
  fileName: string | null;
  /** Why it was not scheduled — unsupported type, too large, unreadable. */
  reason: string | null;
  /**
   * The whole recorded line, for the places that want the operator to see the record verbatim
   * rather than split across columns. Rebuilt from the sanitised halves rather than kept raw, so
   * a skipped attachment still cannot put a path on the screen. Null only when the entry carried
   * nothing usable at all.
   */
  displayText: string | null;
}

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
  /**
   * Attachments the door refused to schedule — ING-06. Deliberately NOT merged into
   * {@link components}: nothing was queued for them, so counting them as parts would put the
   * assembly totals at odds with the work the pipeline actually has.
   *
   * It is also the legacy/pre-assembly record: a canonical assembly reports a refused attachment
   * as a component row with state `Skipped`, but an older message has only this list, and without
   * it the UI silently drops the sole durable evidence that an attachment was missed. Parsed
   * rather than kept as raw text so the name and the reason can be shown apart — see
   * {@link SkippedAttachment.displayText} for the verbatim line.
   */
  skippedAttachments: SkippedAttachment[];
  /**
   * False when the payload carried no skipped-attachment array at all. "Nothing was skipped" and
   * "this deployment does not say" are different claims, and only the first one may be stated.
   */
  skippedAttachmentsReported: boolean;
  /** SUPPLIER_QUOTE / SUPPLIER_INVOICE — set when the message was routed rather than extracted. */
  commercialDocumentTypeHint: string | null;
  /** True when the message continues an existing thread (In-Reply-To / References present). */
  threadContinuation: boolean | null;
  /** The lead this message produced, when one exists. */
  leadId: number | null;
  /** How many line items came out of the body. */
  extractedItemCount: number | null;
  decidedOn: string | null;

  // ---- Assembly. Read through {@link describeAssemblyState}, never compared raw. ----
  /** Raw server state; null when this deployment does not report assembly at all. */
  assemblyState: string | null;
  /** Why assembly stopped, failed, or is waiting for a person. */
  assemblyReason: string | null;
  /** How many parts this message is expected to produce, and how many are finished. */
  componentsExpected: number | null;
  componentsCompleted: number | null;
  /**
   * Every physical part of the message. NULL on the list and populated by {@link getMessage}:
   * "not asked for" and "asked, and this message genuinely has no parts" are different facts and
   * the screen must not conflate them — hence {@link componentsReported}.
   */
  components: EmailComponent[];
  /** False when the payload carried no component array — distinct from "carried an empty one". */
  componentsReported: boolean;
  /** True only when the original RFC822 message is durably addressable. Null is not reported. */
  rawEvidenceStored: boolean | null;
  /** True when reads are checked against the capture-time SHA-256. */
  rawEvidenceVerifiable: boolean | null;
  /** Sender-stamped time and the time extraction completed, when the backend reports them. */
  senderSentOn: string | null;
  parsedOn: string | null;
  /** When the message was durably captured, as distinct from when the mailbox happened to poll. */
  ingestedOn: string | null;
  /**
   * When the pipeline last moved this message. Deliberately NOT called a recovery timestamp:
   * nothing durable distinguishes a recovery sweep from the worker's own barrier, and both write
   * this same value. With the state, it is how "stuck" is told apart from "busy".
   */
  lastUpdatedOn: string | null;
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
/**
 * Reads one component. The list endpoint and the assembly worker have historically disagreed on
 * key spelling (`fileName` vs `filename`, `state` vs `status`), so every field accepts the known
 * aliases rather than letting one rename blank out the whole panel.
 */
const readComponent = (payload: unknown, index: number): EmailComponent => {
  const root = isRecord(payload) ? payload : {};
  const id = asCount(root.id) ?? asCount(root.componentId);
  return {
    key: id !== null ? `component-${id}` : `component-index-${index}`,
    ordinal: asCount(root.ordinal),
    fileName: asText(root.fileName) ?? asText(root.filename) ?? asText(root.name),
    kind: asText(root.kind) ?? asText(root.componentKind) ?? asText(root.type) ?? asText(root.componentType),
    state: asText(root.state) ?? asText(root.componentState) ?? asText(root.status),
    // The operator-safe sentence first; the stable code is the fallback, and the page humanises it
    // rather than printing raw snake_case at a salesperson.
    reason:
      asText(root.reason) ??
      asText(root.failureReason) ??
      asText(root.reviewReason) ??
      asText(root.reasonCode),
  };
};

const isPrintable = (character: string): boolean => {
  const codePoint = character.codePointAt(0) ?? 0;
  return codePoint > 0x1f && !(codePoint >= 0x7f && codePoint <= 0x9f);
};

const MAX_FILE_NAME = 120;

/**
 * The sender chose this name, so it is read as hostile text: only the last segment survives — a
 * skipped attachment must never render as a path — and the length is bounded so one absurd name
 * cannot take the panel over.
 */
const safeFileName = (value: string | null): string | null => {
  const trimmed = asText(value);
  if (trimmed === null) return null;
  const leaf = (trimmed.split(/[\\/]/).pop() ?? '').split('').filter(isPrintable).join('').trim();
  if (leaf.length === 0) return null;
  return leaf.length <= MAX_FILE_NAME ? leaf : `${leaf.slice(0, MAX_FILE_NAME - 1)}…`;
};

/**
 * Splits one recorded entry — `"deck.pptx (unsupported file type '.pptx')"` — into its name and
 * its reason. That "name (reason)" string is the whole wire shape: the column stores a JSON array
 * of them, so this is the only place the two halves can be told apart.
 *
 * The split is the FIRST bracket opening a group that closes exactly at the end of the entry.
 * Neither end is safe to anchor on blindly: a reason carries brackets of its own ("exceeds the
 * 10 MB size limit (12345678 bytes)") and so does an everyday file name ("quote (1).pdf").
 */
const splitSkippedEntry = (entry: string): { fileName: string; reason: string | null } => {
  for (let open = entry.indexOf(' ('); open >= 0; open = entry.indexOf(' (', open + 1)) {
    let depth = 0;
    for (let scan = open + 1; scan < entry.length; scan += 1) {
      if (entry[scan] === '(') depth += 1;
      else if (entry[scan] === ')') {
        depth -= 1;
        if (depth > 0) continue;
        if (scan === entry.length - 1) {
          return { fileName: entry.slice(0, open), reason: asText(entry.slice(open + 2, scan)) };
        }
        break;
      }
    }
  }
  return { fileName: entry, reason: null };
};

/**
 * Reads one entry. A malformed one is kept as an unnamed skip rather than filtered away: the
 * count of attachments that will not be quoted is the entire point of the record, and dropping
 * the entries this build cannot parse is the same disappearance the record exists to prevent.
 */
const readSkippedAttachment = (payload: unknown, index: number): SkippedAttachment => {
  const entry = asText(payload);
  if (entry === null) {
    return { key: `skipped-${index}`, fileName: null, reason: null, displayText: null };
  }
  const split = splitSkippedEntry(entry);
  // Falling back to the whole entry keeps an unsplittable line visible under its own text.
  const fileName = safeFileName(split.fileName) ?? safeFileName(entry);
  const reason = split.reason;
  return {
    key: `skipped-${index}`,
    fileName,
    reason,
    // Recomposed from the sanitised name, never from the wire string: the halves are what the
    // sender controls, and the leaf-only rule has already been applied to them.
    displayText:
      fileName === null ? reason : reason === null ? fileName : `${fileName} (${reason})`,
  };
};

export const readTriageRow = (payload: unknown): EmailTriageRow => {
  const root = isRecord(payload) ? payload : {};
  const componentsRaw = Array.isArray(root.components)
    ? root.components
    : Array.isArray(root.assemblyComponents)
      ? root.assemblyComponents
      : null;
  const attachmentNamesRaw = Array.isArray(root.attachmentNames)
    ? root.attachmentNames
    : Array.isArray(root.attachmentFileNames)
      ? root.attachmentFileNames
      : null;
  // `skippedAttachmentReasons` is the older payload's spelling of the same list. Reading only one
  // of the three names is how a legacy message's skips reach the browser and render as nothing.
  const skippedRaw = Array.isArray(root.skippedAttachments)
    ? root.skippedAttachments
    : Array.isArray(root.skippedAttachmentNames)
      ? root.skippedAttachmentNames
      : Array.isArray(root.skippedAttachmentReasons)
        ? root.skippedAttachmentReasons
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
    skippedAttachments: (skippedRaw ?? []).map(readSkippedAttachment),
    skippedAttachmentsReported: skippedRaw !== null,
    commercialDocumentTypeHint: asText(root.commercialDocumentTypeHint) ?? asText(root.documentTypeHint),
    threadContinuation: asFlag(root.threadContinuation),
    // `assembledLeadId` is the assembly worker's name for the same lead the triage row calls
    // `leadId`. Reading only one of them is how an assembled message shows no way through to it.
    leadId: asCount(root.leadId) ?? asCount(root.assembledLeadId),
    extractedItemCount: asCount(root.extractedItemCount) ?? asCount(root.itemCount),
    decidedOn: asText(root.decidedOn) ?? asText(root.triageDecidedOn),

    assemblyState: asText(root.assemblyState) ?? asText(root.assemblyStatus),
    assemblyReason: asText(root.assemblyReason) ?? asText(root.assemblyStateReason),
    componentsExpected: asCount(root.expectedComponentCount) ?? asCount(root.componentsExpected),
    componentsCompleted: asCount(root.completedComponentCount) ?? asCount(root.componentsCompleted),
    components: (componentsRaw ?? []).map(readComponent),
    componentsReported: componentsRaw !== null,
    rawEvidenceStored: asFlag(root.rawEvidenceStored),
    rawEvidenceVerifiable: asFlag(root.rawEvidenceVerifiable),
    senderSentOn: asText(root.senderSentAtUtc) ?? asText(root.senderSentOn),
    parsedOn: asText(root.parsedAt) ?? asText(root.parsedOn),
    ingestedOn: asText(root.ingestedAtUtc) ?? asText(root.ingestedOn),
    lastUpdatedOn: asText(root.lastUpdatedAtUtc) ?? asText(root.lastUpdatedOn),
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

/** One mailbox the poll could not read. */
export interface MailPollFailure {
  /** The mailbox address. Never a credential — the server does not return those. */
  mailbox: string | null;
  /** Server-authored sentence; the page passes it through the product's error-copy gate. */
  reason: string | null;
  lastSuccessfulPollOn: string | null;
}

/**
 * What one manual poll actually did.
 *
 * Every count is nullable because a deployment may not report it, and a count this screen invents
 * is worse than one it omits — "0 rejected" and "rejections not reported" are different claims.
 */
export interface MailPollReport {
  /** The server's own sentence. Present on every branch, including the "nothing polled" one. */
  message: string | null;
  mailboxesChecked: number | null;
  mailboxesFailed: number | null;
  /** Messages in the poll window, whether or not this cycle could take them. */
  messagesFound: number | null;
  /** Messages this cycle actually downloaded, and how many were already ingested before. */
  messagesNew: number | null;
  alreadyIngested: number | null;
  /** Messages durably stored with their original — the point of no loss. */
  captured: number | null;
  /** Parts handed to extraction. These become leads later, not during the poll. */
  scheduled: number | null;
  needsReview: number | null;
  rejected: number | null;
  /**
   * Messages left unread for the next cycle. Non-zero means mail is still sitting in the mailbox
   * that this poll could not take, which is the one number a "0 new messages" summary would hide.
   */
  notAcknowledged: number | null;
  failures: MailPollFailure[];
  /**
   * Days of mail older than the poll window that were not read, across every mailbox. Non-zero
   * means messages exist that this poll could not see at all.
   */
  lookbackCappedDays: number | null;
  /**
   * False when the mailbox count was absent — the "no active IMAP mailbox is configured" branch,
   * which the server answers with 200. Nothing was polled and nothing ever will be until a mailbox
   * is connected, so this can never be presented as a successful poll (ING-08).
   */
  anyMailboxPolled: boolean;
}

const readPollFailure = (payload: unknown): MailPollFailure => {
  const root = isRecord(payload) ? payload : {};
  return {
    mailbox: asText(root.mailbox) ?? asText(root.emailAddress) ?? asText(root.configurationName),
    reason: asText(root.reason) ?? asText(root.failureReason) ?? asText(root.message),
    lastSuccessfulPollOn: asText(root.lastSuccessfulPoll) ?? asText(root.lastSuccessfulPollOn),
  };
};

/**
 * Reads the poll response.
 *
 * Counts live under `totals`; `mailboxes` and `newMessages` stay at the top level because the All
 * Inquiries toolbar reads them there. Both shapes are accepted so neither side can break the other
 * by moving a field. Failures come from `reasons` (the 502 branch) or, failing that, from the
 * per-mailbox `polled` array — a mailbox that reports `succeeded: false` is a failure whichever
 * branch carried it.
 */
export const readPollReport = (payload: unknown): MailPollReport => {
  const root = isRecord(payload) ? payload : {};
  const totals = isRecord(root.totals) ? root.totals : {};
  const perMailbox = Array.isArray(root.polled) ? root.polled : [];
  const failuresRaw = Array.isArray(root.reasons)
    ? root.reasons
    : Array.isArray(root.failures)
      ? root.failures
      : perMailbox.filter((entry) => isRecord(entry) && entry.succeeded === false);
  const mailboxesChecked = asCount(totals.mailboxesPolled) ?? asCount(root.mailboxes);
  // Summed rather than read from a total, because the cap is reported per mailbox only. Absent
  // everywhere stays null: "no mailbox capped" and "nobody said" are different claims.
  const cappedDays = perMailbox
    .map((entry) => (isRecord(entry) ? asCount(entry.lookbackCappedDays) : null))
    .filter((days): days is number => days !== null);

  return {
    message: asText(root.message),
    mailboxesChecked,
    mailboxesFailed: asCount(totals.mailboxesFailed),
    messagesFound: asCount(totals.messagesFound),
    messagesNew: asCount(totals.messagesDownloaded) ?? asCount(root.newMessages),
    alreadyIngested: asCount(totals.messagesAlreadyIngested),
    captured: asCount(totals.messagesCaptured),
    scheduled: asCount(totals.componentsScheduled),
    needsReview: asCount(totals.messagesHeldForReview),
    rejected: asCount(totals.messagesRejected),
    notAcknowledged: asCount(totals.messagesNotAcknowledged),
    failures: failuresRaw.map(readPollFailure),
    lookbackCappedDays: cappedDays.length > 0 ? Math.max(...cappedDays) : null,
    anyMailboxPolled: mailboxesChecked !== null && mailboxesChecked > 0,
  };
};

/**
 * The poll's failure branch is an HTTP error (502) whose body still carries a usable report: which
 * mailboxes refused and why. Returned so the page can name them instead of showing a bare
 * "request failed", which tells an operator nothing about which inbox to go and fix.
 *
 * Null when the error carries no structured report — a network drop, a 403, a proxy HTML page.
 */
export const readPollFailureReport = (error: unknown): MailPollReport | null => {
  if (!isRecord(error)) return null;
  const response = isRecord(error.response) ? error.response : undefined;
  const data = response?.data;
  if (!isRecord(data)) return null;
  const hasReport = Array.isArray(data.reasons) || Array.isArray(data.failures) || isRecord(data.totals);
  if (!hasReport) return null;
  return readPollReport(data);
};

/** The RFC 7807 `type` the entitlement filter stamps on a feature refusal. */
const FEATURE_NOT_ENTITLED = 'feature-not-entitled';

/**
 * True when the deployment simply does not expose triage here (backend lock not shipped, or the
 * tenant's plan does not include Email Intake). Rendered as an explanation rather than as a
 * failure — nothing is broken, the feature is just not switched on.
 *
 * The 403 branch matters as much as the 404 one. An unentitled tenant got the generic
 * permission-denied copy — "ask an administrator if you need it" — which sends the reader to a
 * person who cannot grant it: an entitlement is a plan-level switch, not a role. Matching on the
 * problem TYPE and not on the bare 403 is deliberate, so an ordinary role denial keeps its own
 * (correct) wording.
 */
export const isTriageUnavailable = (error: unknown): boolean => {
  if (!isRecord(error)) return false;
  const response = isRecord(error.response) ? error.response : undefined;
  const status = typeof response?.status === 'number' ? response.status : undefined;
  if (status === 404 || status === 501) return true;
  if (status !== 403) return false;
  const data = isRecord(response?.data) ? response.data : undefined;
  return typeof data?.type === 'string' && data.type.includes(FEATURE_NOT_ENTITLED);
};

/**
 * Whether "Reprocess as inquiry" can possibly succeed for this message, and — when it cannot —
 * the sentence that says why in the reader's own words.
 *
 * <p>The button used to be rendered on every row. On most of them the endpoint answers 422, and
 * it answered with the raw state name, so a salesperson pressing the only control on the screen
 * was told the message "is already NeedsReview". This is the same shape as the
 * `isInfrastructureHold` gate on the ingestion-batch screen: decide once, render the control only
 * where it works, and say plainly what to do instead.</p>
 *
 * <p>The server remains the authority — this predicate mirrors its rule, it does not replace it.
 * An assembly state this build does not recognise is treated as reopenable rather than hidden:
 * refusing a real recovery because the wording drifted is the more expensive mistake.</p>
 */
export interface ReopenAbility {
  canReopen: boolean;
  /** Null exactly when {@link canReopen} is true. */
  disabledReason: string | null;
}

export const describeReopenAbility = (state: string | null): ReopenAbility => {
  // Mail that predates the assembly aggregate carries no state at all. Its reopen path is the
  // legacy "rejected as noise" one, which is still live, so the control stays.
  if (state === null) return { canReopen: true, disabledReason: null };
  // Through the SAME alias table describeAssemblyState uses. Reading `completed` as an unknown
  // state here would put the button back on a message that already became an inquiry.
  const token = stateToken(state);
  switch (ASSEMBLY_STATE_ALIASES[token] ?? token) {
    case 'noinquiry':
    case 'failedrecoverable':
      return { canReopen: true, disabledReason: null };
    case 'assembled':
      return {
        canReopen: false,
        disabledReason: 'This message already became an inquiry. Open the lead instead.',
      };
    case 'rejectedsecurity':
      return {
        canReopen: false,
        disabledReason:
          'This message was refused on security grounds, so it cannot be sent back through '
          + 'extraction. Contact support if you believe the refusal is wrong.',
      };
    case 'needsreview':
      return {
        canReopen: false,
        disabledReason: 'This message is already waiting for a person to look at it.',
      };
    case 'captured':
    case 'inspecting':
    case 'extracting':
    case 'readyforassembly':
      return {
        canReopen: false,
        disabledReason: 'This message is still being processed, so there is nothing to send back yet.',
      };
    default:
      return { canReopen: true, disabledReason: null };
  }
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
   * One message with every part it was made of.
   *
   * The list deliberately leaves `components` null — it is the same audit surface at a different
   * zoom level, and loading every part of every message to render a collapsed table would be paid
   * for on every page view. The screen asks for this only when a row is opened.
   */
  getMessage: async (id: number): Promise<EmailTriageRow> => {
    const response = await axiosInstance.get(`/api/email-triage/${id}`);
    return readTriageRow(response.data);
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

  /**
   * Polls the configured mailboxes now, instead of waiting for the background cycle.
   *
   * The same endpoint backs the "Sync email" button on All Inquiries, which reads only the two
   * counts its toolbar snackbar needs (`leadService.fetchEmails`). This reader keeps the whole
   * report, because Email Intake has to state what the poll DID — found, captured, scheduled,
   * flagged, rejected — rather than claim a green result over a mailbox that refused to answer.
   */
  pollMailboxes: async (): Promise<MailPollReport> => {
    const response = await axiosInstance.post('/api/Email/fetch');
    return readPollReport(response.data);
  },
};

export default emailTriageService;
