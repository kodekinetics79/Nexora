using ERP_RFQ_Automation.SupplierEvaluation;

namespace ERP_RFQ_Automation.Procurement;

public interface IProcurementApplicationService
{
    Task<SourcingCaseView> CreateOrOpenSourcingCaseAsync(CreateSourcingCaseCommand command, CancellationToken ct = default)
        => throw new NotSupportedException("Sourcing Cases are not supported by this procurement adapter.");
    Task<SourcingCaseView> GetSourcingCaseAsync(long businessUnitId, long sourcingCaseId, CancellationToken ct = default)
        => throw new NotSupportedException("Sourcing Cases are not supported by this procurement adapter.");
    Task<SourcingCandidateSearchResult> SearchSourcingCandidatesAsync(SearchSourcingCandidatesCommand command, CancellationToken ct = default)
        => throw new NotSupportedException("Supplier candidate search is not supported by this procurement adapter.");
    Task<PreparedSupplierRfqResult> PrepareSupplierRfqAsync(PrepareSupplierRfqCommand command, CancellationToken ct = default)
        => throw new NotSupportedException("Supplier RFQ preparation is not supported by this procurement adapter.");
    Task<QueuedSupplierRfqResult> QueuePreparedSupplierRfqAsync(QueuePreparedSupplierRfqCommand command, CancellationToken ct = default)
        => throw new NotSupportedException("Prepared Supplier RFQ dispatch is not supported by this procurement adapter.");
    Task<ProcurementWorkbench> GetWorkbenchAsync(long businessUnitId, long rfqId, CancellationToken ct = default);
    Task<IReadOnlyCollection<SupplierPurchaseOrderSummary>> SearchPurchaseOrdersAsync(
        long businessUnitId, string? search, int limit, CancellationToken ct = default);
    Task<SolicitationResult> CreateSolicitationAsync(CreateSolicitationCommand command, CancellationToken ct = default);
    Task<SolicitationResult> RetrySolicitationAsync(RetrySolicitationCommand command, CancellationToken ct = default);

    /// <summary>
    /// Records that a Supplier RFQ reached the supplier by a route Nexora did not drive — a phone
    /// call, a hand-over, the supplier's own portal, or the buyer's own mailbox — and advances the
    /// solicitation to Sent on the strength of that record.
    ///
    /// <para>The response-capture guard requires a solicitation that actually reached the supplier,
    /// and it stays exactly as it is. Until this existed the only thing that could satisfy it was
    /// the dispatch worker delivering an email, so a buyer holding a price taken over the phone —
    /// or working on any deployment without outbound mail configured — had no way to record it,
    /// while the Supplier Quote Inbox happily wrote the same canonical revision with no email at
    /// all. This gives the buyer a legitimate way to satisfy the guard rather than a way around
    /// it.</para>
    /// </summary>
    Task<RecordedSolicitationDeliveryResult> RecordSolicitationDeliveryAsync(
        RecordSolicitationDeliveryCommand command, CancellationToken ct = default)
        => throw new NotSupportedException(
            "Recorded Supplier RFQ delivery is not supported by this procurement adapter.");

    Task<SupplierQuoteResult> CaptureSupplierQuoteAsync(CaptureSupplierQuoteCommand command, CancellationToken ct = default);
    Task<QuoteComparisonResult> CompareQuotesAsync(long businessUnitId, long rfqItemId, CancellationToken ct = default);
    Task<AwardResult> ApproveAwardAsync(ApproveAwardCommand command, CancellationToken ct = default);
    Task<PurchaseOrderResult> CreatePurchaseOrderAsync(CreatePurchaseOrderCommand command, CancellationToken ct = default);

    /// <summary>
    /// FR-SPO-01. Authorises a draft supplier purchase order for release, recording who approved it
    /// and when.
    ///
    /// <para>Without this step a draft went straight to the supplier: the only thing standing
    /// between an auto-drafted order and committed spend was the Orders/Edit permission of whoever
    /// clicked issue, and no row anywhere named a buyer who had accepted the commitment. Approval
    /// is the named decision; release is the dispatch that follows it.</para>
    /// </summary>
    Task<PurchaseOrderApprovalResult> ApprovePurchaseOrderAsync(
        ApprovePurchaseOrderCommand command, CancellationToken ct = default)
        => throw new NotSupportedException("Purchase order approval is not supported by this procurement adapter.");

    Task<PurchaseOrderResult> IssuePurchaseOrderAsync(IssuePurchaseOrderCommand command, CancellationToken ct = default);

    Task<SupplierPurchaseOrderAcknowledgementResult> AcknowledgePurchaseOrderAsync(
        AcknowledgeSupplierPurchaseOrderCommand command, CancellationToken ct = default);

    Task<PurchaseOrderTradeTermsResult> AmendPurchaseOrderTradeTermsAsync(
        AmendPurchaseOrderTradeTermsCommand command, CancellationToken ct = default);

    /// <summary>
    /// Cancels a purchase order that will never be received, releasing the supply it committed.
    ///
    /// <para>Without this there was no code path anywhere that assigned
    /// <see cref="SupplierPurchaseOrderStatuses.Cancelled"/>. A DRAFT purchase order whose supplier
    /// quotes had lapsed could never be issued and never be cancelled, yet it kept suppressing the
    /// net sourcing requirement for its RFQ line — so re-sourcing threw "already fully covered" and
    /// the customer line became permanently unfulfillable with no operator recourse.</para>
    /// </summary>
    Task<PurchaseOrderResult> CancelPurchaseOrderAsync(CancelPurchaseOrderCommand command, CancellationToken ct = default)
        => throw new NotSupportedException("Purchase order cancellation is not supported by this procurement adapter.");

    Task<GoodsReceiptResult> PostGoodsReceiptAsync(PostGoodsReceiptCommand command, CancellationToken ct = default);
}

public sealed record CreateSourcingCaseCommand(
    long BusinessUnitId,
    long RfqId,
    long RfqItemId,
    int SearchLimit,
    bool SourceEntireQuantity,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record SearchSourcingCandidatesCommand(
    long BusinessUnitId,
    long SourcingCaseId,
    int Limit,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record PrepareSupplierRfqCommand(
    long BusinessUnitId,
    long SourcingCaseId,
    long SupplierId,
    DateTime? DueOn,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record QueuePreparedSupplierRfqCommand(
    long BusinessUnitId,
    long SourcingCaseId,
    long SupplierSolicitationId,
    long ExpectedSourcingCaseVersion,
    long ExpectedSolicitationVersion,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record QueuedSupplierRfqResult(
    long SourcingCaseId,
    long SupplierSolicitationId,
    string Status,
    long SourcingCaseVersion,
    long SolicitationVersion,
    bool Replayed);

public sealed record SourcingCaseView(
    long Id,
    long CommercialDemandLineId,
    long RfqId,
    long RfqItemId,
    string NexoraSerial,
    long? ProductId,
    string? RequestedPartNumber,
    string Description,
    decimal RequestedQuantity,
    decimal StockQuantity,
    decimal UnfulfilledQuantity,
    DateTime? RequiredOn,
    int SearchLimit,
    string Status,
    string NextAction,
    long Version,
    IReadOnlyCollection<SourcingCandidateView> Candidates);

public sealed record SourcingCandidateView(
    long Id,
    long SupplierId,
    string SupplierName,
    string? ContactEmail,
    int Rank,
    string EvidenceType,
    string RecommendationReason,
    decimal EvidenceScore,
    DateTime? EvidenceFreshOn,
    bool Selected,
    string GovernanceStatus,
    string VerificationStatus,
    string ComplianceStatus,
    string RiskStatus,
    string ReadinessStatus,
    bool EligibleForSupplierRfq,
    IReadOnlyCollection<string> BlockingReasons);

public sealed record SourcingCandidateSearchResult(
    long SourcingCaseId,
    int RequestedLimit,
    int ResultCount,
    long Version,
    bool Replayed,
    IReadOnlyCollection<SourcingCandidateView> Candidates);

public sealed record PreparedSupplierRfqResult(
    long SourcingCaseId,
    long SupplierSolicitationId,
    string Status,
    long SourcingCaseVersion,
    long SolicitationVersion,
    bool Replayed);

public sealed record CreateSolicitationCommand(
    long BusinessUnitId,
    long RfqId,
    long SupplierId,
    IReadOnlyCollection<long> RfqItemIds,
    DateTime? DueOn,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record CaptureSupplierQuoteCommand(
    long BusinessUnitId,
    long SolicitationId,
    string SupplierQuoteReference,
    int Revision,
    DateTime ValidUntil,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    IReadOnlyCollection<CaptureSupplierQuoteLine> Lines,
    // The trade term the Supplier quoted on. Trailing and optional so existing callers keep
    // working, but not decorative: the canonical revision this command writes had no Incoterm at
    // all, so the cost-completeness warning — "this is an EXW quote recording no duty" — could
    // never fire for anything captured on the buyer workbench. An unrecorded term is recorded as
    // unrecorded, never guessed.
    string? Incoterms = null);

public sealed record CaptureSupplierQuoteLine(
    long RfqItemId,
    long? ProductId,
    decimal Quantity,
    decimal UnitPrice,
    long CurrencyId,
    int? LeadTimeDays,
    decimal? AvailableQuantity,
    decimal FreightCost,
    decimal DutyCost,
    decimal OtherCost,
    decimal TaxAmount,
    decimal DiscountAmount,
    decimal? MinimumOrderQuantity,
    decimal? ReliabilitySnapshot,
    // FR-QTM-03's fourth criterion, as a number the buyer types on the workbench. Trailing and
    // optional so every existing caller keeps working and so "not captured" stays expressible — a
    // warranty nobody recorded is recorded as not recorded, never as zero months. It is written
    // straight onto the canonical supplier quote line; nothing derives it from the warranty text.
    int? WarrantyMonths = null);

public sealed record RetrySolicitationCommand(
    long BusinessUnitId,
    long SolicitationId,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

/// <summary>
/// A buyer's statement that the Supplier RFQ reached the supplier some way other than by a Nexora
/// email.
///
/// <para><paramref name="Note"/> is mandatory and is the point of the whole command: it is the only
/// account of a conversation Nexora never saw. <paramref name="Actor"/> is the authenticated user
/// recording it and is never taken from a request body. The timestamp is the server's, not the
/// caller's — a delivery is recorded when it is recorded.</para>
/// </summary>
public sealed record RecordSolicitationDeliveryCommand(
    long BusinessUnitId,
    long SolicitationId,
    string DeliveryChannel,
    string Note,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

/// <summary>
/// A delivery a buyer recorded by hand, projected for the workbench.
///
/// <para>Kept structurally separate from the email delivery fields on
/// <see cref="SolicitationView"/> (<c>ProviderReference</c>, <c>AttemptCount</c>,
/// <c>LastErrorCode</c>) so no screen or report can render a phone call as a provider-confirmed
/// email. A solicitation has at most one of the two.</para>
/// </summary>
public sealed record RecordedSolicitationDeliveryView(
    string Channel,
    string Note,
    string RecordedBy,
    DateTime RecordedOn);

public sealed record RecordedSolicitationDeliveryResult(
    long SolicitationId,
    string Status,
    string Channel,
    string Note,
    string RecordedBy,
    DateTime RecordedOn,
    long Version,
    bool Replayed);

public sealed record ApproveAwardCommand(
    long BusinessUnitId,
    long SupplierQuotedItemId,
    decimal Quantity,
    long ExpectedQuoteVersion,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    long? AwardedByUserId,
    string? Rationale);

public sealed record CreatePurchaseOrderCommand(
    long BusinessUnitId,
    long RfqId,
    long SupplierId,
    long CurrencyId,
    long WarehouseId,
    DateOnly ExpectedOn,
    IReadOnlyCollection<long> AwardIds,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    string? Incoterm = null,
    string? PortOfLoading = null,
    string? PortOfDischarge = null);

/// <summary>
/// FR-SPO-06. Corrects the shipping and customs terms of an order that has not yet gone to the
/// supplier.
///
/// <para>Amendment stops at dispatch on purpose: once the PDF is with the supplier, the Incoterm
/// they hold and the Incoterm we hold must not silently diverge. Changing terms after that is a
/// re-issue, not an edit.</para>
///
/// <para>Line entries are optional and sparse — a caller sends only the lines it is changing, so
/// correcting one HS code does not require restating the whole order.</para>
/// </summary>
public sealed record AmendPurchaseOrderTradeTermsCommand(
    long BusinessUnitId,
    long PurchaseOrderId,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    string? Incoterm = null,
    string? PortOfLoading = null,
    string? PortOfDischarge = null,
    IReadOnlyCollection<PurchaseOrderLineTradeTerms>? Lines = null);

public sealed record PurchaseOrderLineTradeTerms(long LineId, string? HsCode, string? CountryOfOrigin);

public sealed record PurchaseOrderTradeTermsResult(
    long Id,
    string Number,
    string? Incoterm,
    string? PortOfLoading,
    string? PortOfDischarge,
    IReadOnlyCollection<PurchaseOrderLineTradeTerms> Lines,
    long Version,
    bool Replayed);

/// <summary>
/// FR-SPO-01. <paramref name="ApprovedByUserId"/> is the identity the segregation-of-duties rule is
/// enforced on, and is separate from <paramref name="Actor"/>: the actor is the audited display
/// name, which a rename can change, while the user id is the key compared against the award
/// approver. An approval that cannot name its user id cannot be checked, and is refused.
/// </summary>
public sealed record ApprovePurchaseOrderCommand(
    long BusinessUnitId,
    long PurchaseOrderId,
    long ExpectedVersion,
    long? ApprovedByUserId,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record IssuePurchaseOrderCommand(
    long BusinessUnitId,
    long PurchaseOrderId,
    long ExpectedVersion,
    string DeliveryEvidenceReference,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    string? DeliveryEvidenceSha256 = null,
    DateTime? DeliveredOn = null);

public sealed record CancelPurchaseOrderCommand(
    long BusinessUnitId,
    long PurchaseOrderId,
    long ExpectedVersion,
    string Reason,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

/// <summary>
/// FR-SPO-03. The supplier's answer to a dispatched order: accepted, rejected, or accepted on
/// different terms.
///
/// <para><paramref name="AcknowledgedBy"/> names the supplier-side person who answered, and is
/// distinct from <paramref name="Actor"/>, the internal user who recorded it. Nexora has no
/// supplier portal, so a buyer keys in what the supplier said by phone or email; conflating the
/// two would attribute the supplier's commitment to our own staff.</para>
///
/// <para>A counter that names neither a revised lead time nor a committed ship date says nothing,
/// and a rejection without a reason cannot be acted on, so both are refused rather than stored as
/// an empty acknowledgement that merely silences the escalation sweep.</para>
/// </summary>
public sealed record AcknowledgeSupplierPurchaseOrderCommand(
    long BusinessUnitId,
    long PurchaseOrderId,
    long ExpectedVersion,
    string AcknowledgementStatus,
    string AcknowledgedBy,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    int? RevisedLeadTimeDays = null,
    DateOnly? CommittedShipDate = null,
    string? Note = null);

public sealed record SupplierPurchaseOrderAcknowledgementResult(
    long Id,
    string Number,
    string Status,
    string AcknowledgementStatus,
    string AcknowledgedBy,
    DateTime AcknowledgedOn,
    int? RevisedLeadTimeDays,
    DateOnly? CommittedShipDate,
    string? Note,
    long Version,
    bool Replayed);

public sealed record PostGoodsReceiptCommand(
    long BusinessUnitId,
    long PurchaseOrderId,
    long WarehouseId,
    string ReceiptNumber,
    DateTime ReceivedOn,
    long ExpectedPurchaseOrderVersion,
    IReadOnlyCollection<PostGoodsReceiptLine> Lines,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

/// <summary>
/// FR-MTR-01. <paramref name="Lot"/> is what the operator says about the material on this line —
/// the supplier's batch number, the serial numbers, the country the goods actually came from.
///
/// <para>Optional with a default so every existing construction site keeps compiling, and because
/// it is genuinely absent for untracked bulk material: the receipt still produces a lot, it is just
/// identified from the receipt rather than by a number somebody typed. A batch- or serial-tracked
/// product with no declaration is refused by the recorder rather than quietly given a made-up
/// identifier.</para>
/// </summary>
public sealed record PostGoodsReceiptLine(
    long PurchaseOrderLineId,
    decimal Quantity,
    Traceability.ReceiptLotDeclaration? Lot = null);

public sealed record ProcurementWorkbench(
    long RfqId,
    string RfqNumber,
    string? NexoraSerial,
    string? CustomerName,
    string? CurrencyCode,
    IReadOnlyCollection<SourcingLineView> Lines,
    IReadOnlyCollection<SolicitationView> Solicitations,
    IReadOnlyCollection<SupplierOfferView> Offers,
    IReadOnlyCollection<SourcingAwardView> Awards,
    IReadOnlyCollection<SupplierPurchaseOrderView> PurchaseOrders,
    CustomerQuoteDraftView? CustomerQuoteDraft);

public sealed record CustomerQuoteDraftView(long QuoteId, string QuoteNumber, long? CurrencyId,
    IReadOnlyCollection<CustomerQuoteDraftLineView> Lines);
public sealed record CustomerQuoteDraftLineView(long QuoteItemId, long RfqItemId, decimal Quantity,
    decimal UnitPrice, decimal TotalAmount);

public sealed record SourcingLineView(long Id, long RfqId, long? ProductId, long? SourcingCaseId, string? PartNumber,
    string Description, decimal RequestedQuantity, decimal AvailableQuantity, decimal ReservedQuantity,
    decimal ShortfallQuantity, DateTime? RequiredOn, string Resolution, DateTime ResolutionCheckedOn);
/// <summary>
/// <paramref name="ProviderReference"/>, <paramref name="AttemptCount"/> and
/// <paramref name="LastErrorCode"/> describe a Nexora email delivery and come from the dispatch
/// outbox. <paramref name="RecordedDelivery"/> describes a delivery the buyer made personally and
/// comes from <c>supplier_solicitation_delivery_records</c>. A solicitation carries at most one of
/// the two, and they are separate fields so that a caller cannot present a phone call as a
/// provider-confirmed email.
/// </summary>
public sealed record SolicitationView(long Id, long RfqId, long SupplierId, string SupplierName,
    string? SupplierEmail, IReadOnlyCollection<long> RfqItemIds, string Status, string Channel,
    int AttemptCount, string? ProviderReference,
    string? LastErrorCode, DateTime? SentOn, DateTime? RespondedOn, DateTime UpdatedOn, long Version,
    DateTime? DueOn = null,
    RecordedSolicitationDeliveryView? RecordedDelivery = null);
public sealed record SupplierOfferView(long Id, long SolicitationId, long RfqItemId, long SupplierId,
    string SupplierName, string? QuoteReference, int QuoteRevision, long CurrencyId, string CurrencyCode,
    decimal Quantity, decimal? AvailableQuantity, decimal UnitPrice, decimal FreightCost, decimal DutyCost,
    decimal OtherCost, decimal? LandedUnitCost, int? LeadTimeDays, decimal? ReliabilitySnapshot,
    DateTime? ValidUntil, bool Eligible, IReadOnlyCollection<string> BlockingReasons, bool Awarded, long Version,
    // Non-blocking cost-completeness warnings — see QuoteComparisonLine.CostWarnings. Carried on
    // the offer as well as the comparison because the workbench is where the buyer looks before
    // deciding, and an incomplete cost build-up that nobody is shown is priced silently.
    IReadOnlyCollection<string>? CostWarnings = null,
    string? Incoterm = null);
public sealed record SourcingAwardView(long Id, long RfqItemId, long SupplierQuotedItemId, long SupplierId,
    string SupplierName, decimal Quantity, decimal LandedUnitCost, long CurrencyId, string CurrencyCode,
    string Status, string? Rationale, long? PurchaseOrderId, long Version);
/// <summary>
/// <paramref name="TrackingMode"/> is <c>SERIAL</c>, <c>LOT</c> or <c>UNTRACKED</c>, computed from the
/// product exactly as <c>Traceability.MaterialLotTrackingModes.FromProduct</c> computes it at receipt
/// time. It is on this view because the receipt screen has to ask for what the recorder will demand:
/// without it, turning on a product's batch or serial switch made every goods receipt for that
/// product throw inside the receipt transaction, naming a field no screen offered.
/// </summary>
public sealed record SupplierPurchaseOrderLineView(long Id, long RfqItemId, long ProductId, string Description,
    decimal OrderedQuantity, decimal ReceivedQuantity, decimal OpenQuantity, decimal UnitCost,
    decimal LandedUnitCost, long WarehouseId, string TrackingMode,
    string? HsCode = null, string? CountryOfOrigin = null);
/// <summary>
/// FR-SPO-01. <c>ApprovedBy</c> and <c>ApprovedOn</c> are optional so that ISSUED rows raised before
/// approval existed still project — they are genuinely unapproved, and rendering them as blank is
/// the truth rather than a gap to be filled in.
/// </summary>
public sealed record SupplierPurchaseOrderView(long Id, long RfqId, string PurchaseOrderNumber,
    long SupplierId, string SupplierName, long CurrencyId, string CurrencyCode, string Status,
    decimal TotalValue, DateOnly? ExpectedOn, long Version, IReadOnlyCollection<SupplierPurchaseOrderLineView> Lines,
    string? ApprovedBy = null, DateTime? ApprovedOn = null,
    // FR-SPO-03 and FR-SPO-06 read path. Without these the buyer records a counter or a rejection,
    // nothing on screen changes, and the only feedback is a 409 the next time they try. The revised
    // lead time and the rejection reason are the entire point of capturing an acknowledgement, so a
    // view that omits them makes the write path pointless.
    string? AcknowledgementStatus = null, string? AcknowledgedBy = null, DateTime? AcknowledgedOn = null,
    int? RevisedLeadTimeDays = null, DateOnly? CommittedShipDate = null, string? AcknowledgementNote = null,
    string? Incoterm = null, string? PortOfLoading = null, string? PortOfDischarge = null);
public sealed record SupplierPurchaseOrderSummary(
    long Id, string PurchaseOrderNumber, long RfqId, string RfqNumber, string? NexoraSerial,
    long SupplierId, string SupplierName, string CurrencyCode, string Status, decimal TotalValue,
    DateOnly? ExpectedOn, DateTime CreatedOn, int LineCount, decimal OpenQuantity);
public sealed record SolicitationResult(long Id, string Status, bool Replayed);
public sealed record SupplierQuoteResult(IReadOnlyCollection<long> LineIds, bool Replayed);
public sealed record AwardResult(long Id, string Status, decimal LandedUnitCost, bool Replayed);
// Version travels with the result because the next call in the lifecycle — approve, issue, receive —
// is optimistically concurrent against it. Without it a caller must refetch to make one more move.
public sealed record PurchaseOrderResult(long Id, string Number, string Status, bool Replayed, long Version);

/// <summary>
/// FR-SPO-01. Carries the approval back with the policy that was in force when it was granted, so a
/// caller and an auditor read the same fact: an approval taken with segregation of duties switched
/// off is not the same approval as one taken with it on.
/// </summary>
public sealed record PurchaseOrderApprovalResult(
    long Id,
    string Number,
    string Status,
    long ApprovedByUserId,
    string ApprovedBy,
    DateTime ApprovedOn,
    long Version,
    bool SegregationOfDutiesEnforced,
    bool Replayed);
public sealed record GoodsReceiptResult(long Id, string Number, string PurchaseOrderStatus, bool Replayed);

public sealed record QuoteComparisonResult(long RfqItemId, IReadOnlyCollection<QuoteComparisonLine> Lines, long? RecommendedSupplierQuotedItemId);
public sealed record QuoteComparisonLine(
    long SupplierQuotedItemId,
    long SupplierId,
    decimal Quantity,
    decimal? AvailableQuantity,
    decimal UnitPrice,
    decimal? LandedUnitCost,
    long CurrencyId,
    int? LeadTimeDays,
    decimal? Reliability,
    DateTime? ValidUntil,
    IReadOnlyCollection<string> Blockers,
    bool Eligible,
    // Distinct from Blockers on purpose. A blocker refuses the award; a warning says the cost
    // build-up looks incomplete and leaves the buyer to judge it. An EXW quote with no duty
    // recorded is perfectly awardable — it is just probably underpriced, and the buyer is the only
    // one who can say. Defaulted so a caller that constructs a line without warnings gets an empty
    // list rather than a null to dereference.
    IReadOnlyCollection<string>? CostWarnings = null,

    // FR-QTM-03. The tenant's weighted score out of SupplierScoringWeights.MaximumScore, or no
    // score and the reason there is none — never a zero, because a zero is indistinguishable from
    // "worst offer in the set" and that confusion is how a missing value becomes a wrong award. An
    // offer with no score stays exactly as awardable as it was: the score ranks and explains, the
    // human awards, and Eligible/Blockers alone decide what may be awarded.
    double? WeightedScore = null,
    // Per-criterion raw value, weight and points earned. Rendered in the row, not behind a hover:
    // a recommendation whose arithmetic the operator cannot check is a black box.
    IReadOnlyCollection<SupplierScoreContribution>? ScoreBreakdown = null,
    string? ScoreUnavailableReason = null,

    // Facts this line already had in hand and dropped. Every one of them comes from the Supplier or
    // the canonical SupplierQuoteRevision that ToComparisonLine is already given — no extra query,
    // no extra join. Without them the comparison table cannot name the supplier it is recommending,
    // and the buyer cannot see that the cheapest row is an alternate part from a different origin.
    string? SupplierName = null,
    // Master data, not governance. Tier annotates and orders; it never gates — award eligibility
    // stays entirely with Blockers/Eligible.
    string? SupplierTier = null,
    string? Manufacturer = null,
    string? PartNumber = null,
    string? SupplierPartNumber = null,
    bool IsAlternate = false,
    string? CountryOfOrigin = null,
    // The supplier's own warranty wording, kept because it carries the conditions the number cannot
    // ("24 months on parts, 12 on labour").
    string? Warranty = null,
    // The typed warranty length, and what the warranty criterion is actually scored from — longer is
    // better. Null means the period was never captured, and under ruling R-F an offer missing a
    // WEIGHTED criterion gets no score and the reason why, never a zero that would sort it last as
    // if the supplier had offered no warranty at all. Shown beside Warranty so the points earned can
    // be checked against the number they came from.
    int? WarrantyMonths = null,
    // The supplier's master-data terms and their numeric companion — the pair the payment-terms
    // criterion is scored from, shown together so the points earned can be checked against the
    // number they were computed from.
    string? PaymentTerms = null,
    int? CreditDays = null,
    // What this quote itself said about payment, which is not always what the supplier record says.
    // Kept separate rather than merged into PaymentTerms: a row that silently mixed two sources
    // would show a figure the score was not computed from.
    string? QuotedPaymentTerms = null);

public sealed class ProcurementValidationException : InvalidOperationException
{
    public ProcurementValidationException(string message) : base(message) { }
}

public sealed class ProcurementConflictException : InvalidOperationException
{
    public ProcurementConflictException(string message) : base(message) { }
}
