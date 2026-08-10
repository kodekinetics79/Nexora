namespace ERP_RFQ_Automation.CommercialCases;

public sealed record CommercialCaseSearchResult(
    long Id,
    string MasterReference,
    long LeadId,
    string? CustomerRfqNumber,
    string? BuyerName,
    string? CustomerEmail,
    string? Status,
    DateTime CreatedOn,
    int RfqCount,
    int QuoteCount,
    int OrderCount,
    int ShipmentCount,
    string MatchReason);

/// <summary>
/// How a document in the timeline relates to the case it is listed under.
///
/// <para>Every document in the timeline is there because it <em>declares</em> this case. The
/// distinction below is whether the legacy foreign-key chain agrees, which is reconciliation
/// evidence and nothing more — the declared key decides membership.</para>
/// </summary>
public static class CommercialCaseLinkStates
{
    /// <summary>The document declares this case and the foreign-key chain reaches it too.</summary>
    public const string Reconciled = "Reconciled";

    /// <summary>
    /// The document declares this case but the foreign-key chain does not reach it. Under the old
    /// reader this document was invisible; it is shown, and the broken join is reported as a gap.
    /// </summary>
    public const string ChainBroken = "ChainBroken";

    /// <summary>
    /// No foreign-key reconciliation is attempted for this document type, so the declared key is
    /// the only statement of membership there is.
    /// </summary>
    public const string DeclaredOnly = "DeclaredOnly";
}

public static class CommercialCaseGapKinds
{
    /// <summary>
    /// A document the foreign-key chain reaches that declares no case at all. Legacy or
    /// bypass-created; it is NOT in the timeline, and this is the record of that.
    /// </summary>
    public const string Unlinked = "UnlinkedDocument";

    /// <summary>
    /// A document the foreign-key chain reaches that declares a DIFFERENT case. It is excluded
    /// from this timeline — the wrong case id is the defect, not the exclusion.
    /// </summary>
    public const string ConflictingCase = "ConflictingCase";

    /// <summary>
    /// A document that declares this case but that the foreign-key chain cannot reach. It IS in
    /// the timeline; the broken join is reported so the two views can be reconciled.
    /// </summary>
    public const string ChainBroken = "ChainBroken";

    /// <summary>
    /// FR-COM-07. A supplier purchase order raised against customer demand that names no customer
    /// document — no client PO, no sales order, no quotation. It IS in the timeline, because it
    /// declares the case; what is missing is the ability to answer "which customer was this bought
    /// for?" from the order itself rather than by re-joining through the RFQ.
    ///
    /// <para>A STOCK replenishment order never produces this gap: it has no customer, so absent
    /// keys are the correct answer rather than a defect.</para>
    /// </summary>
    public const string CustomerOriginMissing = "CustomerOriginMissing";
}

/// <summary>
/// A disagreement between what a document declares and what the legacy foreign-key chain says.
/// Never suppressed and never silently repaired: a reader that hides these is worse than one that
/// walks the chain, because it makes an incomplete spine look complete.
/// </summary>
public sealed record CommercialCaseTraceabilityGap(
    string DocumentType,
    long DocumentId,
    string Reference,
    string GapKind,
    long? DeclaredCommercialCaseId,
    string Detail);

public sealed record CommercialCaseDocument(
    string DocumentType,
    long DocumentId,
    string Reference,
    string? Status,
    DateTime? OccurredOn,
    long? ParentDocumentId = null,
    string LinkState = CommercialCaseLinkStates.DeclaredOnly);

public sealed record CommercialCaseStatusEvent(
    long Id,
    string EventType,
    string? PreviousStatus,
    string? NewStatus,
    string? ChangedBy,
    string ActorSource,
    DateTime ChangedOn,
    string? Reason,
    string? AggregateType = null,
    string? CorrelationId = null,
    string? RequestReference = null,
    string? PolicyVersion = null,
    string? ReasonCode = null);

public sealed record CommercialCaseDetail(
    long Id,
    string MasterReference,
    long AllocationNumber,
    long BusinessUnitId,
    DateTime CreatedOn,
    long LeadId,
    string? CustomerRfqNumber,
    string? BuyerName,
    string? CustomerEmail,
    string? OpportunityNumber,
    string? CurrentStatus,
    IReadOnlyList<CommercialCaseDocument> Documents,
    IReadOnlyList<CommercialCaseStatusEvent> StatusHistory,
    IReadOnlyList<CommercialCaseTraceabilityGap> TraceabilityGaps);
