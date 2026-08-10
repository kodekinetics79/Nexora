namespace ERP_RFQ_Automation.SupplierQuotes;

public static class SupplierQuoteInboxStatuses
{
    public const string ReviewRequired = "REVIEW_REQUIRED";
    public const string ReadyForComparison = "READY_FOR_COMPARISON";
}

public static class SupplierQuoteReviewStatuses
{
    public const string Required = "REQUIRED";
    public const string Accepted = "ACCEPTED";
    public const string Corrected = "CORRECTED";
    public const string Rejected = "REJECTED";
}

public static class SupplierQuoteCaptureChannels
{
    public const string Manual = "MANUAL";
    public const string Offline = "OFFLINE";
    public const string Email = "EMAIL";
    public const string Upload = "UPLOAD";
    public const string Portal = "PORTAL";
    public const string Api = "API";
}

public static class SupplierNegotiationDispositions
{
    public const string Prepared = "PREPARED";
    public const string Deferred = "DEFERRED";
    public const string Dismissed = "DISMISSED";
}

/// <summary>Stable identity for all revisions of one Supplier Quote.</summary>
public sealed class SupplierQuote
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SupplierId { get; set; }
    public long SupplierSolicitationId { get; set; }
    public long SourcingCaseId { get; set; }
    public long RfqId { get; set; }
    public string NexoraSerial { get; set; } = null!;
    public string SupplierQuoteReference { get; set; } = null!;
    public int CurrentRevisionNumber { get; set; }
    public string InboxStatus { get; set; } = SupplierQuoteInboxStatuses.ReviewRequired;
    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTime UpdatedOn { get; set; }
    public string UpdatedBy { get; set; } = null!;
    public ICollection<SupplierQuoteRevision> Revisions { get; set; } = new List<SupplierQuoteRevision>();
}

/// <summary>Immutable snapshot of one response or negotiation round from a Supplier.</summary>
public sealed class SupplierQuoteRevision
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SupplierQuoteId { get; set; }
    public int RevisionNumber { get; set; }
    public string CaptureChannel { get; set; } = null!;
    public long? SourceDocumentId { get; set; }
    public string SourceIdentity { get; set; } = null!;
    public string SourceSha256 { get; set; } = null!;
    public long CurrencyId { get; set; }
    public DateTime? ValidUntil { get; set; }
    public string? Incoterms { get; set; }
    public decimal FreightAmount { get; set; }
    public decimal TaxAmount { get; set; }

    /// <summary>
    /// Import duty the buyer will pay to bring this round in, as the buyer already knows it.
    ///
    /// <para>Decision R8 rules out DERIVING duty from an HS code — no rate engine, no Incoterm cost
    /// derivation. It does not rule out RECORDING the amount. R8's reasoning ("duty is already
    /// inside the price") holds for a customer price a rep types; it is false for a supplier
    /// quoting FOB or EXW, who by definition has not paid KSA duty and has not put it in the price.
    /// With this column at a hard zero, a 5% duty at a 20% target margin underpriced every import
    /// by 6.25% — <c>1 / (1 - 0.2) - 1</c> applied to the missing 5%.</para>
    /// </summary>
    public decimal DutyAmount { get; set; }

    /// <summary>Clearance, handling, inspection and the like: irrecoverable, and cost of the goods.</summary>
    public decimal OtherAmount { get; set; }

    /// <summary>
    /// A round-level discount the supplier granted. Subtracted from landed cost, because it is
    /// money the goods did not cost. It used to be dropped entirely on the way into the canonical
    /// revision, so a re-projection silently priced without it.
    /// </summary>
    public decimal DiscountAmount { get; set; }
    public string? PaymentTerms { get; set; }
    public string? Notes { get; set; }
    public bool RequiresReview { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public DateTime CapturedOn { get; set; }
    public string CapturedBy { get; set; } = null!;
    public string CorrelationId { get; set; } = null!;
    public SupplierQuote SupplierQuote { get; set; } = null!;
    public ICollection<SupplierQuoteLine> Lines { get; set; } = new List<SupplierQuoteLine>();
    public ICollection<SupplierQuoteFieldEvidence> Evidence { get; set; } = new List<SupplierQuoteFieldEvidence>();
    public ICollection<SupplierQuoteReviewDecision> ReviewDecisions { get; set; } = new List<SupplierQuoteReviewDecision>();
    public ICollection<SupplierNegotiationDecision> NegotiationDecisions { get; set; } = new List<SupplierNegotiationDecision>();
}

public sealed class SupplierQuoteLine
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SupplierQuoteRevisionId { get; set; }
    public int LineNumber { get; set; }
    public long RfqItemId { get; set; }
    public long CommercialDemandLineId { get; set; }
    public string? PartNumber { get; set; }
    public string? Manufacturer { get; set; }
    public string? SupplierPartNumber { get; set; }
    public string Description { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal? AvailableQuantity { get; set; }
    public string UnitOfMeasure { get; set; } = null!;
    public decimal UnitPrice { get; set; }
    public decimal? MinimumOrderQuantity { get; set; }
    public int? LeadTimeDays { get; set; }
    public string? AvailabilityType { get; set; }
    public string? OriginCountry { get; set; }
    public string? Warranty { get; set; }
    public bool IsAlternate { get; set; }
    public string? Exceptions { get; set; }
    public ICollection<SupplierQuoteFieldEvidence> Evidence { get; set; } = new List<SupplierQuoteFieldEvidence>();
}

/// <summary>Immutable field-level provenance emitted by extraction or manual capture.</summary>
public sealed class SupplierQuoteFieldEvidence
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SupplierQuoteRevisionId { get; set; }
    public long? SupplierQuoteLineId { get; set; }
    public string FieldName { get; set; } = null!;
    public string? OriginalValue { get; set; }
    public string? NormalizedValue { get; set; }
    public decimal Confidence { get; set; }
    public string Method { get; set; } = null!;
    public string? ModelOrRuleVersion { get; set; }
    public int? SourcePage { get; set; }
    public string? SourceRegion { get; set; }
    public bool Critical { get; set; }
    public bool ReviewRequired { get; set; }
    public DateTime CreatedOn { get; set; }
}

/// <summary>Append-only reviewer disposition. Original evidence remains unchanged.</summary>
public sealed class SupplierQuoteReviewDecision
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SupplierQuoteRevisionId { get; set; }
    public long SupplierQuoteFieldEvidenceId { get; set; }
    public string Status { get; set; } = null!;
    public string? CorrectedValue { get; set; }
    public string Reason { get; set; } = null!;
    public string ReviewedBy { get; set; } = null!;
    public DateTime ReviewedOn { get; set; }
    public string CorrelationId { get; set; } = null!;
}

/// <summary>
/// Append-only human disposition of a shadow negotiation recommendation. It records
/// evidence and intent only; it cannot send, award, or change commercial values.
/// </summary>
public sealed class SupplierNegotiationDecision
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SupplierQuoteId { get; set; }
    public long SupplierQuoteRevisionId { get; set; }
    public string RecommendationCode { get; set; } = null!;
    public string Disposition { get; set; } = null!;
    public string Reason { get; set; } = null!;
    public string EvidenceSnapshotJson { get; set; } = "{}";
    public string PolicyVersion { get; set; } = null!;
    public long ExpectedQuoteVersion { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public string Actor { get; set; } = null!;
    public DateTime DecidedOn { get; set; }
    public string CorrelationId { get; set; } = null!;
    public SupplierQuote SupplierQuote { get; set; } = null!;
    public SupplierQuoteRevision SupplierQuoteRevision { get; set; } = null!;
}

/// <summary>Immutable bridge from an approved supplier award to one customer Quote line.</summary>
public sealed class CustomerQuoteSourcingDecision
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long QuoteId { get; set; }
    public long QuoteItemId { get; set; }
    public long RfqId { get; set; }
    public long RfqItemId { get; set; }
    public long CommercialDemandLineId { get; set; }
    public long SourcingCaseId { get; set; }
    public long SourcingAwardId { get; set; }
    public long SupplierQuotedItemId { get; set; }
    public long SupplierQuoteId { get; set; }
    public long SupplierQuoteRevisionId { get; set; }
    public long SupplierQuoteLineId { get; set; }
    public string NexoraSerial { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal SupplierLandedUnitCost { get; set; }
    public decimal TargetMarginPercent { get; set; }
    public decimal CustomerUnitPrice { get; set; }
    public long CurrencyId { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public string Rationale { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public string CorrelationId { get; set; } = null!;
}
