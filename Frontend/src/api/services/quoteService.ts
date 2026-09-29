import axiosInstance from '../axiosInstance';
import { downloadAuthenticatedFile } from '../../utils/authenticatedFile';

/**
 * What the server did when asked to remove a quotation. `deleted` is false for the normal case —
 * anything past DRAFT is withdrawn and kept, because the customer holds a document with those
 * numbers on it and the price attestations behind it are evidence, not clutter.
 */
export interface QuoteRemovalResult {
  quoteNo: string;
  mode: 'DRAFT_DISCARDED' | 'WITHDRAWN';
  removedOn: string;
  deleted: boolean;
  message: string;
}

/**
 * One priced line of a quote, mirroring `DTOs/QuoteDTOs/QuoteResponseDTO.cs`.
 *
 * This was `any[]` until the tax fields went missing on one of the two server-side projections and
 * nothing on this side could have noticed. `any` on a wire contract is how a field the server sends
 * ends up read by no screen, and how a field no server sends ends up read by one.
 *
 * Optional means "the server may legitimately omit it", not "we did not check".
 */
export interface QuoteLineDTO {
  id: number;
  quoteId: number;
  rfqItemId?: number | null;
  productId?: number | null;
  productName?: string | null;
  itemDescription?: string | null;
  quantity: number;
  unitOfMeasure?: string | null;
  customerLineRef?: string | null;
  unitPrice: number;
  /** Tax-INCLUSIVE line total (calculation version 2): `taxableBase + taxAmount`. */
  totalAmount: number;
  discount?: number | null;
  discountTypeId?: number | null;
  discountTypeName?: string | null;
  discountValue?: number | null;
  /** Server-derived. Null means the tax was never derived, which is what blocks the send. */
  taxAmount?: number | null;
  taxCategory?: string | null;
  taxCategoryReason?: string | null;
  taxRatePercentApplied?: number | null;
  /**
   * This line's share of the QUOTE-LEVEL discount, as the server allocated and stored it.
   * It cannot be recovered from the other figures — every attempt subtracts a tax-inclusive total
   * from a tax-exclusive net — so a screen showing the header discount has to be told it.
   * Null on lines written before the column existed.
   */
  headerDiscountAllocated?: number | null;
  /** What output tax was charged on: `totalAmount - taxAmount`. The printed line column's figure. */
  taxableBase: number;
  deliveryLeadTime?: number | null;
  /** Quantity offered ex stock when the balance follows in deliveryLeadTime days. */
  exStockQuantity?: number | null;
  /** ESTIMATE (priced, subject to confirmation), TO_FOLLOW (price follows), NOT_QUOTED (with a reason), or null for a plain price. */
  pricingStatus?: 'ESTIMATE' | 'TO_FOLLOW' | 'NOT_QUOTED' | null;
  pricingNote?: string | null;
  /** "Offered: GE THQL32010, replaces ABB AF96" when the part offered is not the one asked for. */
  offeredNote?: string | null;
  offeredSpecs?: string | null;
  /**
   * What the buyer calls this line, as the QUOTE stores and prints it (their material number, the
   * maker and part number they asked for). Null on hand-typed and older lines; screens then fall
   * back to the requested* values below.
   */
  customerMaterialCode?: string | null;
  manufacturerName?: string | null;
  manufacturerPartNumber?: string | null;
  // What the customer asked for, read through the linked RFQ line. Null when the quote has no RFQ.
  requestedManufacturerName?: string | null;
  requestedManufacturerPartNumber?: string | null;
  requestedItemMaterialCode?: string | null;
  requestedAlternatePartNumber?: string | null;
  requestedDeliveryDate?: string | null;
  requestedLeadTimeDays?: number | null;
  requestedCurrency?: string | null;
}

export interface QuoteDTO {
  id: number;
  quoteNo: string;
  rfqId?: number;
  rfqNo?: string;
  leadId?: number;
  sourceLeadRevision: number;
  sourceRfqRevision: number;
  revisionImpact?: string | null;
  /**
   * The open customer revision in full: which revision arrived, which one this quote was built
   * from, and what changed on each line. Detail projection only; `revisionImpact` above is the
   * type string every existing branch reads.
   */
  revisionImpactDetail?: QuoteRevisionImpactDTO | null;
  commercialCaseId?: number;
  commercialCaseReference?: string | null;
  nexoraSerial?: string | null;
  lifecycleVersion: number;
  version: number;
  customerId?: number;
  contactId?: number | null;
  contactName?: string | null;
  customerName?: string;
  businessUnitId: number;
  businessUnitName: string;
  customerEmail?: string;
  quoteDate: string;
  validUntil: string;
  statusId: number;
  statusValue: string;
  currencyId?: number;
  currencyCode?: string;
  totalAmount: number;
  headerRemarks?: string;
  createdBy: string;
  createdDate: string;
  modifiedBy?: string;
  modifiedDate?: string;
  discountTypeId?: number;
  discountTypeName?: string;
  discountValue?: number;
  itemCount: number;
  quoteItems: QuoteLineDTO[];
  // Outcome capture + SLA staleness (WP-A4)
  statusCode?: string;
  sentOn?: string | null;
  respondedOn?: string | null;
  outcomeOn?: string | null;
  outcomeReasonId?: number | null;
  outcomeReasonName?: string | null;
  outcomeNote?: string | null;
  isStale?: boolean;
  daysSinceSent?: number | null;
  /** Set once a later revision was SENT: the customer holds that one, so this quote no longer counts. */
  supersededByQuoteNo?: string | null;
  /** A revision of this quote not sent yet: the customer still holds this one; its status moves once the revision goes out. */
  pendingRevisionId?: number | null;
  pendingRevisionQuoteNo?: string | null;
  /** The number printed on a quote made outside Nexora. */
  externalQuoteReference?: string | null;
  /** Set when the quote was made outside Nexora and uploaded: the file the customer holds. */
  uploadedFileName?: string | null;
  /**
   * The client's own status on top of the fixed one (Setup > Quote statuses): a customer step
   * while the quote is SENT ("Technical evaluation"), or an ending once it closed ("Partly won").
   * Null when none was picked. The fixed status (statusCode) still drives everything else.
   */
  subStatusId?: number | null;
  subStatusName?: string | null;
  subStatusKind?: 'STEP' | 'ENDING' | null;
  subStatusOn?: string | null;
  /** The person who owns the quote (Quote.OwnerUserId), by name. Null when nobody does. */
  ownerName?: string | null;
  // Reasoned validity extensions (R7)
  /** When the validity date was last moved by an explicit, reasoned extend command. */
  validityExtendedOn?: string | null;
  /** Lifecycle permits extending: sent to the customer, no outcome recorded yet. */
  canExtendValidity?: boolean;
}

export interface OutcomeReasonDTO {
  id: number;
  code: string;
  label: string;
}

// ==== Customer revisions on a quote ====

/** One changed fact on one line of the customer's document, in the buyer's own line numbers. */
export interface QuoteRevisionLineChangeDTO {
  line: string;
  /** quantity · unit · part · description · added · removed · changed */
  field: string;
  from?: string | null;
  to?: string | null;
}

/** Mirrors `DTOs/QuoteDTOs/QuoteRevisionImpactDTOs.cs`. */
export interface QuoteRevisionImpactDTO {
  impactId: number;
  impactType: string;
  /** The lead revision this quote was prepared from. */
  fromRevision: number;
  /** The lead revision that arrived and made the quote stale. */
  toRevision: number;
  changes: QuoteRevisionLineChangeDTO[];
}

/** What "Apply the new quantities" did to the draft. */
export interface QuoteRevisionApplyResult {
  quoteId: number;
  fromRevision: number;
  toRevision: number;
  linesUpdated: number;
  applied: QuoteRevisionLineChangeDTO[];
  /** Lines the revision added that the draft does not have — nothing was invented for them. */
  linesNotOnQuote: string[];
  totalAmount?: number | null;
  /** RFQ lines that took the new quantities too (D-05). */
  rfqLinesUpdated?: QuoteRevisionLineChangeDTO[];
  /** Open supplier requests that asked for the old quantity — ask those suppliers again. */
  outdatedSupplierRequests?: OutdatedSupplierRequest[];
}

export interface OutdatedSupplierRequest {
  solicitationId: number;
  supplierRfqNumber?: string | null;
  supplierName?: string | null;
  line: string;
  askedQuantity: number;
  newQuantity: number;
}

// ==== The covering e-mail (mirrors DTOs/QuoteDTOs/QuoteEmailDraftDTOs.cs) ====

export interface QuoteEmailDraft {
  quoteId: number;
  quoteNo: string;
  recipientEmail?: string | null;
  subject: string;
  /** Plain text; blank lines separate paragraphs. */
  body: string;
  attachmentFileName: string;
}

/** What the rep changed in the send dialog. Either field left undefined keeps the server default. */
export interface QuoteEmailMessage {
  subject?: string;
  body?: string;
}

// ==== Below-floor holds (WP-B3) + revisions-lite (WP-B4) ====

/** Result of a send attempt: either it went out, or it was parked in Approvals. */
export interface QuoteSendOutcome {
  held: boolean;
  /** Plain-language hold info when held ("Quote #…: N line(s) below floor by up to X%"). */
  message?: string;
  approvalId?: string;
  /**
   * R5: nothing was sent because the price source is unconfirmed, or a price changed after
   * it was last confirmed. The rep must confirm again before the quote can go out.
   */
  priceAttestationRequired?: boolean;
  /**
   * R17: nothing was sent because a line's output tax was never calculated — this business unit has
   * no output tax rate configured, the line has not been priced, or a non-standard tax treatment
   * carries no stated reason. A quotation with no VAT shown on it is treated as tax-inclusive, so
   * the difference would come out of the seller's margin.
   */
  taxDerivationRequired?: boolean;
  /**
   * The customer HAS the quote: the delivery worker had already completed this send (the server
   * replayed it). Only this justifies telling the rep "emailed".
   */
  delivered?: boolean;
  /**
   * A delivery row was written and a background worker will send it later. Nothing has reached
   * the customer yet, and the worker can still refuse (no transmitting mailbox, a render failure)
   * — in which case the fixed delivery key makes this quote number permanently unsendable. The
   * screen must not call this "emailed".
   */
  queuedForDelivery?: boolean;
}

/**
 * The words a rep is shown for a send the server accepted. Shared by every screen that sends a
 * quote, so they cannot drift from each other or from the server's own distinction.
 *
 * Until this existed, `sendEmail` discarded the 202 body — `{ queuedForDelivery, delivered }` —
 * and every accepted send was announced as "Quote emailed to the customer". Almost every send is
 * merely QUEUED: the row is handed to `QuoteDeliveryWorker`, which can dead-letter it minutes
 * later with nobody watching. A rep who read "emailed" closed the tab; the customer had nothing.
 */
export const describeQuoteSendOutcome = (
  result: Pick<QuoteSendOutcome, 'delivered' | 'queuedForDelivery'> & { replayed?: boolean },
): { delivered: boolean; message: string } =>
  // A quote has one delivery. Sending it "again" with the same words replays that delivery: the
  // customer already has it and nothing new went out, so the rep is told exactly that.
  result.delivered && result.replayed
    ? { delivered: true, message: 'Already emailed to the customer. Nothing new was sent.' }
    : result.delivered
    ? { delivered: true, message: 'Quote emailed to the customer' }
    : {
        delivered: false,
        message: 'Quote queued for delivery. It is not with the customer yet — the status changes to Sent once the email is confirmed.',
      };

// ==== Price-provenance attestation (Decision Register R5) ====

/** Where a quoted price came from. These are the only two the server accepts. */
export type PriceAttestationSource = 'SALES_MANAGER' | 'SUPPLIER_QUOTE';

export interface QuotePriceAttestationLine {
  quoteItemId: number;
  rfqItemId?: number | null;
  itemDescription?: string | null;
  quantity: number;
  unitPrice: number;
}

/** Whether this quote may be sent, and what was last confirmed. */
/** One reason a quote's send would be refused, and the screen that fixes it. */
export interface QuoteSendBlocker {
  /** Stable code for branching. Never render this. */
  code: string;
  /** The sentence the rep reads: what is wrong AND what to do. */
  message: string;
  setupLabel?: string | null;
  setupPath?: string | null;
}

/** Something to know before sending that never blocks it (owner rule: inform, don't obstruct). */
export interface QuoteSendWarning {
  /** BUYER_REVISION_NEWER · VALIDITY_BELOW_BUYER_MINIMUM · BID_CLOSED · LINES_NOT_FIRM · LEAD_TIME_MISSING · CURRENCY_NOT_ALLOWED. Never render. */
  code: string;
  message: string;
  /** VALIDITY_BELOW_BUYER_MINIMUM: the earliest date the buyer accepts (yyyy-MM-dd…). */
  suggestedValidUntil?: string | null;
  /** BUYER_REVISION_NEWER: which revision the quote reflects, which one arrived, what changed. */
  revision?: QuoteRevisionImpactDTO | null;
  /** BUYER_REVISION_NEWER: the new quantities can be applied in one click. */
  canApply?: boolean;
}

/** The buyer's commercial terms from the RFQ document (validity floor, currencies, delivery terms). */
export interface QuoteBuyerTerms {
  minimumValidityDays?: number | null;
  validityBasis?: 'CLOSING' | 'SUBMISSION' | 'UNSTATED' | null;
  validitySentence?: string | null;
  requiredValidUntil?: string | null;
  bidClosing?: string | null;
  allowedCurrencies: string[];
  currencySentence?: string | null;
  deliveryTerms?: string | null;
  deliverTo?: string | null;
  agreement?: string | null;
  payment?: string | null;
}

export interface QuoteSendReadiness {
  quoteId: number;
  canSend: boolean;
  blockers: QuoteSendBlocker[];
  /** Never counted in canSend. */
  warnings?: QuoteSendWarning[];
  buyerTerms?: QuoteBuyerTerms | null;
  /**
   * UNCERTAIN when a previous delivery was interrupted and never confirmed — the customer may
   * already hold this quote, and nothing is resent automatically. NOT_DELIVERED when it
   * definitively failed. Null when no delivery has ended terminally.
   */
  deliveryOutcome?: 'UNCERTAIN' | 'NOT_DELIVERED' | 'DELIVERED' | null;
  deliveryInFlight?: boolean;
  /** Who the pending mail is addressed to and when it was handed over; null when none is pending. */
  deliveryRecipient?: string | null;
  deliveryRequestedOn?: string | null;
}

export interface QuotePriceAttestationStatus {
  quoteId: number;
  satisfied: boolean;
  /** Why the send would be refused; null when satisfied. */
  reason?: string | null;
  source?: PriceAttestationSource | null;
  sourceReference?: string | null;
  confirmedBy?: string | null;
  confirmedOn?: string | null;
  /** A confirmation exists but a price changed since, so it no longer covers the quote. */
  supersededByPriceChange: boolean;
  currencyId?: number | null;
  currencyCode?: string | null;
  currentLines: QuotePriceAttestationLine[];
  attestedLines: QuotePriceAttestationLine[];
}

export interface QuoteRevisionInfoDTO {
  quoteId: number;
  quoteNo: string;
  revisionNo: number;
  revisionOfQuoteId?: number | null;
  revisionOfQuoteNo?: string | null;
  supersededByQuoteId?: number | null;
  supersededByQuoteNo?: string | null;
  chainLocked: boolean;
  canRevise: boolean;
}

// ==== Reasoned quote-validity extensions (Decision Register R7) ====

/** One recorded move of a quote's validity date, with the reason that justified it. */
export interface QuoteValidityExtensionDTO {
  id: number;
  quoteId: number;
  previousValidUntil?: string | null;
  newValidUntil: string;
  reason: string;
  extendedBy: string;
  extendedOn: string;
}

export interface QuoteValidityExtensionResult {
  quoteId: number;
  quoteNo: string;
  validUntil?: string | null;
  validityExtendedOn?: string | null;
  /** Unchanged by extending — the commercial offer is the same offer. */
  revisionNo: number;
  replayed: boolean;
  extension?: QuoteValidityExtensionDTO | null;
}

export type QuoteOutcome = 'won' | 'lost' | 'expired';

/** What a client status counts as on dashboards, reminders and auto-expiry. */
export type QuoteCountsAs = 'WON' | 'LOST' | 'EXPIRED';

export interface QuoteStatusOption {
  id: number;
  name: string;
  sortOrder: number;
  isActive: boolean;
  /** Seeded by Nexora and not editable (e.g. "Expired automatically"). */
  isSystem: boolean;
}
export interface QuoteEndingOption extends QuoteStatusOption { countsAs: QuoteCountsAs }
export interface QuoteReasonOption extends QuoteStatusOption {
  code: string;
  /** Which outcome the reason is offered for. Null = Lost and Expired (the reasons that existed before). */
  for: QuoteCountsAs | null;
}
/** The client's own quote statuses (Setup > Quote statuses). Won / Lost / Expired themselves are fixed and not listed. */
export interface QuoteStatusCatalog {
  steps: QuoteStatusOption[];
  endings: QuoteEndingOption[];
  reasons: QuoteReasonOption[];
}
export type QuoteStatusKind = 'step' | 'ending' | 'reason';

export interface PaginatedQuotes {
  items: QuoteDTO[];
  totalItems: number;
}

export interface QuoteParams {
  businessUnitId?: number;
  pageNumber?: number;
  pageSize?: number;
  search?: string;
  state?: string;
}

export interface UploadQuoteInput {
  file: File;
  rfqId: number;
  quoteNumber: string;
  sentOn: string;
  validUntil?: string | null;
  currencyId: number;
  /** Before VAT. Nexora adds the VAT at the tenant's rate, as on every quote. */
  amount: number;
}

const quoteService = {
  getAll: async (params: QuoteParams = {}): Promise<PaginatedQuotes> => {
    const { data } = await axiosInstance.get('/api/Quote', { params });
    return data;
  },

  getById: async (id: number, businessUnitId?: number): Promise<QuoteDTO> => {
    const params = businessUnitId ? { businessUnitId } : {};
    const { data } = await axiosInstance.get(`/api/Quote/${id}`, { params });
    return data;
  },

  create: async (quoteData: any): Promise<QuoteDTO> => {
    const { data } = await axiosInstance.post('/api/Quote', quoteData);
    return data;
  },

  update: async (id: number, quoteData: any): Promise<any> => {
    const { data } = await axiosInstance.put(`/api/Quote/${id}`, quoteData);
    return data;
  },

  /**
   * Removes a quotation. `reason` is mandatory and the server refuses without it (400).
   *
   * A quote past DRAFT is WITHDRAWN, not deleted: the row stays on file with its R5 price
   * attestations and R7 validity extensions, and drops out of the quote list and the pipeline
   * stats. Only a clean draft — never attested, never extended, no order — is actually deleted,
   * and a tombstone is written either way. The response says which happened, so surface
   * `message` rather than assuming the record is gone.
   */
  removeQuote: async (
    id: number,
    reason: string,
    businessUnitId?: number,
  ): Promise<QuoteRemovalResult> => {
    const params: Record<string, string | number> = { reason };
    if (businessUnitId) params.businessUnitId = businessUnitId;
    const { data } = await axiosInstance.delete<QuoteRemovalResult>(`/api/Quote/${id}`, { params });
    return data;
  },
  
  /**
   * The commercial document itself. R5: the server refuses to render it unless the quote's
   * current prices are covered by a recorded price attestation (409, `priceAttestationRequired`).
   *
   * Because the request asks for a Blob, axios hands the ERROR body back as a Blob too, so the
   * refusal reason would otherwise be unreadable and every caller would fall back to a generic
   * "download failed". The body is decoded back to JSON here so the rep sees what to do next.
   */
  /**
   * A quote made outside Nexora (by hand, in Excel, on the customer's portal): the file is kept and
   * the quote is recorded on its RFQ as sent on `sentOn`. Dates are `YYYY-MM-DD`. An unsent draft
   * already on the RFQ is replaced, and `replacedDraftNo` names it.
   */
  upload: async (input: UploadQuoteInput): Promise<{ quoteId: number; quoteNo: string; replacedDraftNo?: string | null }> => {
    const form = new FormData();
    form.append('file', input.file);
    form.append('rfqId', String(input.rfqId));
    form.append('quoteNumber', input.quoteNumber.trim());
    form.append('sentOn', input.sentOn);
    if (input.validUntil) form.append('validUntil', input.validUntil);
    form.append('currencyId', String(input.currencyId));
    form.append('amount', String(input.amount));
    const { data } = await axiosInstance.post('/api/Quote/upload', form, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
    return data;
  },

  /** The file of a quote made outside Nexora, saved under its own name. */
  downloadUploadedFile: (id: number, fileName: string): Promise<void> =>
    downloadAuthenticatedFile(`/api/Quote/${id}/uploaded-file`, fileName),

  downloadPdf: async (id: number): Promise<Blob> => {
    try {
      const { data } = await axiosInstance.get(`/api/Quote/${id}/pdf`, { responseType: 'blob' });
      return data;
    } catch (error: any) {
      const body = error?.response?.data;
      if (body instanceof Blob) {
        try {
          error.response.data = JSON.parse(await body.text());
        } catch {
          // Not JSON (a proxy error page, an empty body). Leave it alone rather than
          // inventing a reason — the caller's fallback message is the honest answer.
        }
      }
      throw error;
    }
  },

  /**
   * Sends the quote email. Three 409s mean "nothing was sent", not "it failed":
   *  - WP-B3 `queuedForApproval` — parked in the Approvals inbox (below-floor pricing);
   *  - R5 `priceAttestationRequired` — the price source needs confirming (again);
   *  - R17 `taxDerivationRequired` — a line's output tax has not been calculated.
   * All three are surfaced as outcomes rather than thrown errors.
   */
  /**
   * The covering e-mail the customer would receive if the quote were sent now — the server's own
   * default subject and plain-text body, the attachment name and the address on record — so the
   * rep can review and edit it in the send dialog.
   */
  /**
   * The rep uploaded the PDF to the customer's own procurement portal: record the quote as sent.
   * A 409 carries the reason in `message` (price source, tax, below the minimum, not a draft);
   * it is moved to `detail`, where the screens read an API's reason from.
   */
  recordPortalSubmission: async (id: number, portalReference?: string | null): Promise<{ quoteNo: string; submitted: boolean; alreadySent: boolean }> => {
    try {
      const { data } = await axiosInstance.post(`/api/Quote/${id}/portal-submission`, { portalReference: portalReference?.trim() || null });
      return data;
    } catch (error: any) {
      const data = error?.response?.data;
      if (error?.response?.status === 409 && typeof data?.message === 'string') error.response.data = { ...data, detail: data.message };
      throw error;
    }
  },

  getEmailDraft: async (id: number): Promise<QuoteEmailDraft> => {
    const { data } = await axiosInstance.get(`/api/Quote/${id}/email-draft`);
    return data;
  },

  /**
   * @param message the rep's edited subject/body from the send dialog. Omitted, the server sends
   * its default (the same words `getEmailDraft` returned).
   */
  sendEmail: async (id: number, recipientEmail: string, message?: QuoteEmailMessage): Promise<QuoteSendOutcome> => {
    try {
      // 202 Accepted carries `{ queuedForDelivery, delivered, replayed }`. Read it: "queued" and
      // "delivered" are different facts and the rep is told different things for each.
      const body = message ? { customSubject: message.subject ?? null, customBody: message.body ?? null } : null;
      const { data } = await axiosInstance.post(`/api/Quote/${id}/email`, body, { params: { recipientEmail } });
      return {
        held: false,
        delivered: data?.delivered === true,
        queuedForDelivery: data?.queuedForDelivery === true,
      };
    } catch (error: any) {
      const data = error?.response?.data;
      // Only a string may become user-facing copy — an object here would render as
      // "[object Object]" at the call site.
      const asText = (...values: unknown[]) =>
        values.find((value): value is string => typeof value === 'string' && value.trim().length > 0);

      if (error?.response?.status === 409 && data?.priceAttestationRequired) {
        return { held: false, priceAttestationRequired: true, message: asText(data.message) };
      }
      if (error?.response?.status === 409 && data?.taxDerivationRequired) {
        return { held: false, taxDerivationRequired: true, message: asText(data.message) };
      }
      if (error?.response?.status === 409 && data?.queuedForApproval) {
        return { held: true, message: asText(data.summary, data.message), approvalId: data.approvalId };
      }
      throw error;
    }
  },

  // ==== Send readiness ====

  /**
   * Everything that would refuse this quote's send, answered BEFORE the send dialog opens.
   *
   * Half the send chain runs in a background worker whose refusals reach nobody: a draft with
   * no currency, a business unit with no legal name, and a tenant with no transmitting mailbox
   * all pass every synchronous check, produce a green "queued" toast, and die in the outbox.
   * The delivery idempotency key is fixed per quote, so a dead-lettered row then makes that
   * quote permanently unsendable.
   */
  getSendReadiness: async (id: number): Promise<QuoteSendReadiness> => {
    const { data } = await axiosInstance.get(`/api/Quote/${id}/send-readiness`);
    return data;
  },

  // ==== Price-provenance attestation (R5) ====

  /** Whether the quote may be sent, and the prices a fresh confirmation would cover. */
  getPriceAttestation: async (id: number): Promise<QuotePriceAttestationStatus> => {
    const { data } = await axiosInstance.get(`/api/Quote/${id}/price-attestation`);
    return data;
  },

  /** Records the rep's confirmation over the quote's current prices. */
  confirmPriceAttestation: async (
    id: number,
    source: PriceAttestationSource,
    sourceReference: string,
  ): Promise<QuotePriceAttestationStatus> => {
    const { data } = await axiosInstance.post(`/api/Quote/${id}/price-attestation`, {
      source,
      sourceReference,
    });
    return data;
  },

  // ==== Revisions-lite (WP-B4) ====

  /** Clones a non-draft quote as a new DRAFT revision; 409 when draft/superseded/locked. */
  revise: async (id: number): Promise<QuoteDTO> => {
    const { data } = await axiosInstance.post(`/api/Quote/${id}/revise`);
    return data;
  },

  getRevisionInfo: async (id: number): Promise<QuoteRevisionInfoDTO> => {
    const { data } = await axiosInstance.get(`/api/Quote/${id}/revisions`);
    return data;
  },

  /** "Keep as quoted": the reason is required and recorded with the lines that differ (D-04). */
  resolveRevisionImpact: async (id: number, reason: string): Promise<void> => {
    await axiosInstance.post(`/api/Quote/${id}/revision-impact/resolve`, { reason }, {
      headers: { 'Idempotency-Key': crypto.randomUUID() },
    });
  },

  /**
   * "Apply the new quantities": the draft's lines take the arriving revision's quantities, the
   * draft is re-totalled and the impact is resolved, in one server transaction. 409 on a quote
   * already with the customer — that one is revised, not edited.
   */
  applyRevisionQuantities: async (id: number): Promise<QuoteRevisionApplyResult> => {
    const { data } = await axiosInstance.post(`/api/Quote/${id}/revision-impact/apply`, null, {
      headers: { 'Idempotency-Key': crypto.randomUUID() },
    });
    return data;
  },

  // ==== Reasoned validity extensions (R7) ====

  /**
   * Holds an already-sent quote's price open until a later date. The reason is mandatory and
   * is recorded against the quote, not merely logged. Does NOT create a revision — the buyer
   * is still looking at the same commercial offer.
   *
   * 400 = the date or reason is unusable; 409 = the quote's lifecycle does not allow it
   * (still a draft, already won/lost/expired, or superseded by a newer revision). Both carry
   * a message that says what to do instead.
   */
  extendValidity: async (
    id: number,
    validUntil: string,
    reason: string,
  ): Promise<QuoteValidityExtensionResult> => {
    const { data } = await axiosInstance.post(
      `/api/Quote/${id}/extend-validity`,
      { validUntil, reason },
      { headers: { 'Idempotency-Key': crypto.randomUUID() } },
    );
    return data;
  },

  /** Every recorded validity move on this quote, newest first (R7: the reason must be readable). */
  getValidityExtensions: async (id: number): Promise<QuoteValidityExtensionDTO[]> => {
    const { data } = await axiosInstance.get(`/api/Quote/${id}/validity-extensions`);
    return data;
  },

  transitionStatus: async (id: number, status: string, expectedVersion: number): Promise<unknown> => {
    const operationId = crypto.randomUUID();
    const { data } = await axiosInstance.post(`/api/Quote/${id}/status`, {
      targetStatusCode: status.toUpperCase(),
      expectedVersion,
      correlationId: operationId,
      idempotencyKey: `quote-${id}-${status.toLowerCase()}-${operationId}`,
    });
    return data;
  },

  // ==== Outcome capture (WP-A4) ====

  getOutcomeReasons: async (): Promise<OutcomeReasonDTO[]> => {
    const { data } = await axiosInstance.get('/api/Quote/outcome-reasons');
    return data;
  },

  setOutcome: async (
    id: number,
    outcome: QuoteOutcome,
    reasonCode?: string,
    note?: string,
    /** One of the client's own endings; it must count as `outcome`. */
    endingId?: number | null,
  ): Promise<QuoteDTO> => {
    const { data } = await axiosInstance.post(`/api/Quote/${id}/outcome`, {
      outcome,
      reasonCode: reasonCode || undefined,
      note: note || undefined,
      endingId: endingId || undefined,
    });
    return data;
  },

  /** The client's own statuses. Reps get the active ones; Setup asks for all. */
  getStatusCatalog: async (includeInactive = false): Promise<QuoteStatusCatalog> => {
    const { data } = await axiosInstance.get('/api/Quote/statuses', { params: { includeInactive: includeInactive || undefined } });
    return data;
  },

  /**
   * Sets (or clears, with null) the client's customer step on a SENT quote. Picking a step also
   * records that the customer responded, so the quote stops showing "No reply".
   */
  setStep: async (id: number, stepId: number | null): Promise<QuoteDTO> => {
    const { data } = await axiosInstance.put(`/api/Quote/${id}/step`, { stepId });
    return data;
  },

  /** Setup > Quote statuses: add one of the client's own steps, endings or reasons. */
  addStatusOption: async (kind: QuoteStatusKind, body: { name: string; countsAs?: QuoteCountsAs; for?: QuoteCountsAs | null }) => {
    const { data } = await axiosInstance.post('/api/Quote/statuses', { kind, ...body });
    return data as QuoteStatusOption;
  },

  /** Setup > Quote statuses: rename, move, switch on/off. What an ending counts as never changes. */
  updateStatusOption: async (id: number, body: { name?: string; sortOrder?: number; isActive?: boolean; for?: QuoteCountsAs | null }) => {
    const { data } = await axiosInstance.put(`/api/Quote/statuses/${id}`, body);
    return data as QuoteStatusOption;
  },

  markResponded: async (id: number): Promise<void> => {
    await axiosInstance.post(`/api/Quote/${id}/mark-responded`);
  },
};

export default quoteService;
