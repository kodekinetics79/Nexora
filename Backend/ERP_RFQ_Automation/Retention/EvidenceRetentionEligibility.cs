using ERP_RFQ_Automation.CommercialDocuments;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;

namespace ERP_RFQ_Automation.Retention;

/// <summary>
/// The rules that decide whether a document's bytes may be destroyed, stated once so the
/// selection query, the dry-run estimate and the skip report cannot drift apart.
///
/// <para>
/// Every rule here is enforced in SQL by <see cref="EvidenceRetentionService"/>, never only
/// in the UI. Deletion is irreversible, so an excluded document must be impossible to
/// select — not merely hard to click.
/// </para>
/// </summary>
public static class EvidenceRetentionEligibility
{
    /// <summary>
    /// Document classes carrying statutory retention that outlives any tenant preference:
    /// UAE Commercial Transactions Law and KSA/ZATCA e-invoicing require commercial books,
    /// invoices and orders to be kept for years. Hard-coded rather than a tenant toggle —
    /// a tenant may choose how long to keep intake artefacts, and may not choose to delete
    /// statutory records.
    /// </summary>
    public static readonly IReadOnlyList<CommercialDocumentType> StatutoryDocumentTypes = new[]
    {
        CommercialDocumentType.PurchaseOrder,
        CommercialDocumentType.SupplierInvoice,
        CommercialDocumentType.CustomerOrder,
        CommercialDocumentType.SupplierConfirmation,
        CommercialDocumentType.ReceiptOrDeliveryDocument
    };

    /// <summary>
    /// Intake states that are finished with the bytes. Everything else — including
    /// <see cref="IntakeOccurrenceStatus.DeadLetter"/> — still needs them: a dead-lettered
    /// job is replayable from stored bytes by the security-scan recovery path, and the 40
    /// such jobs in production would become permanently unrecoverable if purged.
    /// </summary>
    public static readonly IReadOnlyList<IntakeOccurrenceStatus> TerminalIntakeStatuses = new[]
    {
        IntakeOccurrenceStatus.Resolved,
        IntakeOccurrenceStatus.Rejected
    };

    /// <summary>Lead states with no live commercial expectation. Anything else — and a lead
    /// with no status at all — counts as an open case and is never auto-purged.</summary>
    public static readonly IReadOnlyList<string> TerminalLeadStatusCodes = new[]
    {
        "LOST", "CANCELLED", "COMPLETED", "DISQUALIFIED", "DUPLICATED"
    };

    /// <summary>Only a succeeded extraction is done with the source bytes. Pending, Failed
    /// and DeadLetter all still need them to retry.</summary>
    public const ExtractionStatus TerminalExtractionStatus = ExtractionStatus.Succeeded;

    /// <summary>Inquiry states that still expect a human to look at the original.</summary>
    public static readonly IReadOnlyList<CanonicalInquiryStatus> OpenInquiryStatuses = new[]
    {
        CanonicalInquiryStatus.Draft,
        CanonicalInquiryStatus.ReviewRequired
    };

    public const string ReasonBytesAlreadyAbsent = "BytesAlreadyAbsent";
    public const string ReasonRetentionElapsed = "RetentionElapsed";
    public const string ReasonSecurityRejected = "SecurityRejected";

    public static class Skip
    {
        public const string NotCompleted = "Extraction has not completed for this document.";
        public const string SecurityPending = "The security verdict is still pending.";
        public const string Quarantined =
            "Quarantined bytes are the malware evidence for this document and are never purged.";
        public const string TooRecent = "The document is younger than the tenant's retention period.";
        public const string AlreadyPurged = "The document's bytes have already been purged.";
        public const string StatutoryDocumentType =
            "Classified as an invoice, purchase order, order or delivery document, which carries statutory retention.";
        public const string OpenIntake = "Intake for this document is not finished; the bytes are still needed.";
        public const string ExtractionNotSucceeded =
            "A bound extraction job has not succeeded and can still be retried from these bytes.";
        public const string OpenInquiry = "A linked inquiry is still in draft or awaiting review.";
        public const string LegalHold = "The document is under legal hold.";
        public const string DeletionNotApproved =
            "Deletion was requested for this document but has not been approved.";
        public const string OpenHumanAction = "An open human action item points at this document.";
        public const string OpenCommercialCase = "A linked lead is still an open commercial case.";
    }

    /// <summary>
    /// The two content-addressed keys the same bytes were written to. Ingestion writes every
    /// document TWICE — once to the <c>quarantine</c> zone before inspection and again to the
    /// <c>cleared</c> zone after it — under different keys, and nothing has ever deleted
    /// either. The sibling key is derived from the stored key by swapping the zone segment
    /// rather than rebuilt from parts, so the extension and hash shard are exactly the ones
    /// on disk and no guess can point the deletion at the wrong object.
    /// </summary>
    public static IReadOnlyList<string> ZoneKeysFor(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            return [];

        var normalized = objectKey.Replace('\\', '/');
        var keys = new List<string> { normalized };
        var sibling = normalized.Contains("/cleared/", StringComparison.Ordinal)
            ? normalized.Replace("/cleared/", "/quarantine/", StringComparison.Ordinal)
            : normalized.Contains("/quarantine/", StringComparison.Ordinal)
                ? normalized.Replace("/quarantine/", "/cleared/", StringComparison.Ordinal)
                : null;
        if (sibling is not null && !string.Equals(sibling, normalized, StringComparison.Ordinal))
            keys.Add(sibling);
        return keys;
    }
}
