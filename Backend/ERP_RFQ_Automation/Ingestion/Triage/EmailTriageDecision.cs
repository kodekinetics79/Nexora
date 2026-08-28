using System;

namespace ERP_RFQ_Automation.Ingestion.Triage;

/// <summary>
/// What the intake door decided to do with one inbound message.
/// </summary>
public enum EmailTriageOutcome
{
    /// <summary>A request for goods/services from (or plausibly from) a customer. Extract it.</summary>
    Inquiry,

    /// <summary>A commercial document that is NOT a customer request (supplier quote, invoice).
    /// It is recorded and routed, but it never becomes a customer Lead.</summary>
    CommercialNonInquiry,

    /// <summary>Machine-verifiable non-business mail: autoreply, bulk/list mail, calendar
    /// message, no-reply sender, or a reply that added no new words. No AI call is made.</summary>
    Noise,

    /// <summary>No positive evidence either way. EXTRACTED AND FLAGGED — never dropped.</summary>
    Uncertain
}

/// <summary>
/// Stable, machine-readable reasons a message was triaged the way it was. They are persisted
/// on the EmailIngest row and rendered as chips in the inbound-mail surface, so a human can
/// see WHY a message was rejected and reprocess it. Never renumber or re-word these strings.
/// </summary>
public static class EmailTriageReasonCodes
{
    // --- noise (positive evidence only) ---
    /// <summary>RFC 3834 <c>Auto-Submitted</c> other than <c>no</c>, or an <c>X-Autoreply</c> header.</summary>
    public const string AutoSubmittedHeader = "auto_submitted_header";
    /// <summary><c>Precedence: bulk|list|junk</c>, <c>List-Id</c> or <c>List-Unsubscribe</c>.</summary>
    public const string BulkListHeader = "bulk_list_header";
    /// <summary>The sender's local part is a no-reply / daemon / postmaster address.</summary>
    public const string NoreplySender = "noreply_sender";

    /// <summary>
    /// The message was sent BY this tenant, from one of its own mailboxes.
    ///
    /// Nexora emails a supplier RFQ from the platform sender; if that sender is also a mailbox
    /// Nexora polls — which it is whenever a tenant tests with its own address, and whenever the
    /// supplier's address happens to be the tenant's — the outbound message arrives back in the
    /// inbox, carries an RFQ reference in its subject, and is classified as an inbound inquiry.
    /// Every supplier request then manufactures a phantom lead, and the lead's own reference is
    /// our solicitation number rather than a customer's.
    /// </summary>
    public const string OwnOutboundMail = "own_outbound_mail";
    /// <summary>Nothing but a quoted thread and/or a signature survived the strip, and there
    /// are no attachments — the sender added no new words.</summary>
    public const string EmptyAfterQuoteStrip = "empty_after_quote_strip";
    /// <summary>An Outlook calendar message (<c>Content-Class: urn:content-classes:calendarmessage</c>).</summary>
    public const string CalendarInvite = "calendar_invite";
    /// <summary>The sender explicitly labels the fresh message as non-RFQ/informational or
    /// states that there is no buying or quotation intent. This is stronger than an RFQ word
    /// mentioned inside that denial, but yields to a separate affirmative request or quantity.</summary>
    public const string ExplicitNonInquiryIntent = "explicit_non_inquiry_intent";

    // --- inquiry ---
    /// <summary>The sender is a known customer contact for this tenant.</summary>
    public const string KnownCustomerContact = "known_customer_contact";
    /// <summary>A quantity written against a unit of measure ("40 nos", "12 pcs", "300 mtr").</summary>
    public const string QtyUomPattern = "qty_uom_pattern";
    /// <summary>The subject names an RFQ/enquiry/tender/ITB.</summary>
    public const string RfqReference = "rfq_reference";
    /// <summary>A conversational request verb ("please quote", "kindly advise").</summary>
    public const string RequestVerb = "request_verb";

    // --- commercial non-inquiry ---
    /// <summary>Supplier-quotation vocabulary from the commercial-document classifier.</summary>
    public const string SupplierQuoteTerms = "supplier_quote_terms";
    /// <summary>Supplier-invoice vocabulary from the commercial-document classifier.</summary>
    public const string InvoiceTerms = "invoice_terms";
    /// <summary>Purchase-order vocabulary. EVIDENCE ONLY — it never routes a message away from
    /// extraction, because in this segment the same counterparty both buys and sells.</summary>
    public const string PoTerms = "po_terms";

    // --- uncertain ---
    /// <summary>Nothing matched in either direction. Extract and flag.</summary>
    public const string NoSignal = "no_signal";

    /// <summary>A human overturned the gate from the inbound-mail surface and replayed the
    /// stored message. The outcome is forced to <see cref="EmailTriageOutcome.Uncertain"/> —
    /// extracted AND flagged — never silently promoted to a clean inquiry.</summary>
    public const string ManualReprocess = "manual_reprocess";
}

/// <summary>Durable checkpoint states owned by the inbound-email orchestration layer.</summary>
public static class EmailTriagePersistenceStatuses
{
    /// <summary>
    /// A governed human override has been committed, but one or more extraction jobs may still
    /// need to be scheduled. The poller must resume with the persisted manual decision instead of
    /// running the deterministic noise rule again.
    /// </summary>
    public const string Reprocessing = "Reprocessing";
}

/// <summary>Commercial-document type hints handed to the extraction worker. Anything that is
/// not a customer RFQ completes the job WITHOUT creating a Lead
/// (<see cref="ERP_RFQ_Automation.Extraction.ExtractionJobMetadata.IsNonLeadCommercialType"/>).</summary>
public static class EmailTriageDocumentHints
{
    public const string SupplierQuote = "SUPPLIER_QUOTE";
    public const string SupplierInvoice = "SUPPLIER_INVOICE";
}

/// <param name="Outcome">What to do with the message.</param>
/// <param name="ReasonCodes">Every reason that fired, in evaluation order. Never empty.</param>
/// <param name="CommercialDocumentTypeHint">Set only for <see cref="EmailTriageOutcome.CommercialNonInquiry"/>.</param>
/// <param name="ThreadContinuation">True when the message is a reply/forward (In-Reply-To or References).</param>
public sealed record EmailTriageDecision(
    EmailTriageOutcome Outcome,
    string[] ReasonCodes,
    string? CommercialDocumentTypeHint,
    bool ThreadContinuation);
