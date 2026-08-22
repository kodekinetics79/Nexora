using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;

namespace ERP_RFQ_Automation.Procurement;

public static class SourcingCaseStatuses
{
    public const string Draft = "DRAFT";
    public const string InternalSearch = "INTERNAL_SEARCH";
    public const string DiscoveryRequired = "DISCOVERY_REQUIRED";
    public const string SuppliersSelected = "SUPPLIERS_SELECTED";
    public const string OutreachReady = "OUTREACH_READY";
    public const string OutreachSent = "OUTREACH_SENT";
    public const string ResponsesPartial = "RESPONSES_PARTIAL";
    public const string ResponsesComplete = "RESPONSES_COMPLETE";
    public const string ComparisonReady = "COMPARISON_READY";
    public const string Negotiation = "NEGOTIATION";
    public const string AwardReview = "AWARD_REVIEW";
    public const string SupplierSelected = "SUPPLIER_SELECTED";
    public const string CustomerQuoteReady = "CUSTOMER_QUOTE_READY";
    public const string Closed = "CLOSED";
    public const string Cancelled = "CANCELLED";
}

public static class SourcingCandidateEvidenceTypes
{
    public const string PreferredSupplier = "PREFERRED_SUPPLIER";
    public const string PriorSupplierQuote = "PRIOR_SUPPLIER_QUOTE";
    public const string PurchaseHistory = "PURCHASE_HISTORY";
    public const string PurchaseOrderHistory = "PURCHASE_ORDER_HISTORY";
    public const string SupplierMetadata = "SUPPLIER_METADATA";
}

/// <summary>
/// Immutable commercial line anchor. RFQ item rows remain authoritative source rows;
/// this identity gives downstream sourcing and quote revisions a stable tenant key.
/// </summary>
public sealed class CommercialDemandLine
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long RfqId { get; set; }
    public long RfqItemId { get; set; }
    public string NexoraSerial { get; set; } = null!;
    public string IdentityKey { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
}

public sealed class SourcingCase
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CommercialDemandLineId { get; set; }
    public long RfqId { get; set; }
    public long RfqItemId { get; set; }
    public long? LeadId { get; set; }
    public long? CustomerId { get; set; }
    public long? ProductId { get; set; }
    public string NexoraSerial { get; set; } = null!;
    public string? RequestedPartNumber { get; set; }
    public string? Manufacturer { get; set; }
    public string Description { get; set; } = null!;
    public string? UnitOfMeasure { get; set; }
    public decimal RequestedQuantity { get; set; }
    public decimal StockQuantity { get; set; }
    public decimal UnfulfilledQuantity { get; set; }
    public DateTime? RequiredOn { get; set; }
    public string? DeliveryLocation { get; set; }
    public int SearchLimit { get; set; } = 10;
    public string Priority { get; set; } = "NORMAL";
    public string Status { get; set; } = SourcingCaseStatuses.Draft;
    public string NextAction { get; set; } = "Review known suppliers";
    public string ShortageDecisionKey { get; set; } = null!;

    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTime UpdatedOn { get; set; }
    public string UpdatedBy { get; set; } = null!;
    public ICollection<SourcingCaseCandidate> Candidates { get; set; } = new List<SourcingCaseCandidate>();
}

/// <summary>Persisted evidence snapshot. Scores describe evidence coverage, never invented supplier performance.</summary>
public sealed class SourcingCaseCandidate
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SourcingCaseId { get; set; }
    public long SupplierId { get; set; }
    public int Rank { get; set; }
    public string EvidenceType { get; set; } = null!;
    public string RecommendationReason { get; set; } = null!;
    public string EvidenceJson { get; set; } = "{}";
    public decimal EvidenceScore { get; set; }
    public DateTime? EvidenceFreshOn { get; set; }
    public bool Selected { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime UpdatedOn { get; set; }
}

/// <summary>
/// FR-SPO-04 names eight states: Draft, Approved, Sent, Acknowledged, In Production, Shipped,
/// Received and Closed. Only five existed, and ISSUED conflated Approved with Sent — so a buyer
/// could not tell an order authorised internally from one the supplier had actually received,
/// and there was nowhere for an acknowledgement to land.
///
/// <para>ISSUED is retained rather than removed: existing rows carry it, and rewriting history to
/// fit a new vocabulary is worse than keeping the old word and mapping it. PARTIALLY_RECEIVED and
/// CANCELLED are additions beyond the eight, recorded as approved because a real order needs
/// both.</para>
/// </summary>
public static class SupplierPurchaseOrderStatuses
{
    public const string Draft = "DRAFT";

    /// <summary>Authorised internally. Not yet with the supplier.</summary>
    public const string Approved = "APPROVED";

    /// <summary>Dispatched to the supplier, awaiting their acknowledgement.</summary>
    public const string Sent = "SENT";

    /// <summary>The supplier has responded — accepted, or countered and been re-agreed.</summary>
    public const string Acknowledged = "ACKNOWLEDGED";

    public const string InProduction = "IN_PRODUCTION";
    public const string Shipped = "SHIPPED";

    /// <summary>Legacy: conflated Approved and Sent. Kept so existing rows stay readable.</summary>
    public const string Issued = "ISSUED";

    /// <summary>
    /// The one fact "this order has left us and is with the supplier", spelled two ways.
    ///
    /// <para>SENT is what release writes now. ISSUED is the same fact under the old vocabulary,
    /// which conflated Approved with Sent; existing rows carry it and rewriting history to fit the
    /// new word would be worse than mapping it, so the two coexist and every guard must treat them
    /// identically. This set is the single place that equivalence is stated — a guard that spells
    /// one of them without the other is the defect, not a variation.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> WithSupplier = new HashSet<string>(StringComparer.Ordinal)
    {
        Sent, Issued
    };

    /// <summary>
    /// Dispatched and not yet settled — the states in which a goods receipt is meaningful.
    ///
    /// <para>SENT and ISSUED are both here, and must stay together: SENT is what release writes and
    /// ISSUED is what rows raised before the split carry. Omitting SENT would mean goods could not
    /// be received against any order dispatched after this gate — the same regression ACKNOWLEDGED
    /// already caused once.</para>
    ///
    /// <para>ACKNOWLEDGED is in this set because a supplier accepting an order moves it forward.
    /// Leaving it out would make the supplier's acceptance the very act that blocks receiving the
    /// goods, which is the sort of transition gap that only shows up in production.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> OpenForReceipt = new HashSet<string>(StringComparer.Ordinal)
    {
        Sent, Issued, Acknowledged, InProduction, Shipped, PartiallyReceived
    };

    /// <summary>
    /// Placed with a supplier and still expected to arrive — the states in which the un-received
    /// quantity on an order is real committed supply and nets off the demand it was raised against.
    ///
    /// <para>This answers a different question from <see cref="OpenForReceipt"/> ("may I book a
    /// receipt against this?") even though the two coincide today. They are kept apart because a
    /// future state could easily belong to one and not the other, and one set answering two
    /// questions is how a change to one of them silently answers the other.</para>
    ///
    /// <para><b>Why every one of these is here.</b> DRAFT and APPROVED are excluded: an order the
    /// supplier has never seen is an internal intention, and counting it as supply is what left a
    /// draft with lapsed quotes suppressing its RFQ line forever. SENT and ISSUED are the same
    /// dispatched fact spelled two ways (<see cref="WithSupplier"/>). ACKNOWLEDGED is here because
    /// leaving it out made the supplier's acceptance the act that erased the coverage — the RFQ line
    /// reopened, a second sourcing case was raised, and the same material was bought twice on the
    /// ordinary happy path. IN_PRODUCTION and SHIPPED are here for that reason in advance: no code
    /// writes them yet, but they are the strongest commitments on the ladder, and if they were
    /// omitted the double-buy would return the day inbound logistics starts writing them.</para>
    ///
    /// <para>RECEIVED, CLOSED and CANCELLED are excluded. Nothing is still expected to arrive
    /// against them. For the first two that is arithmetically free — the outstanding quantity is
    /// already zero — but listing them would state something untrue about what the set means, and
    /// CANCELLED must be out for the coverage to reopen when a buyer withdraws an order.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> CommittedSupply = new HashSet<string>(StringComparer.Ordinal)
    {
        Sent, Issued, Acknowledged, InProduction, Shipped, PartiallyReceived
    };

    /// <summary>
    /// Whether an order still represents material that is genuinely coming, expressed as one
    /// predicate because status alone cannot answer it — decision register R31.
    ///
    /// <para>A supplier who REJECTS an order does not move it off SENT. Nothing in the
    /// acknowledgement path changes <see cref="SupplierPurchaseOrder.Status"/> except an ACCEPTED
    /// answer, so a rejected order stays inside <see cref="CommittedSupply"/> and keeps netting its
    /// full quantity off the RFQ line. Meanwhile both SLA sweeps deliberately skip a rejected order,
    /// because chasing a supplier who has already said no is noise. The combination is the
    /// dangerous part: three alarms go quiet, the line still reports itself covered,
    /// <c>CreateSolicitationAsync</c> still refuses to re-source it as "already fully covered" —
    /// and the material is never bought. Nothing anywhere says so.</para>
    ///
    /// <para>Before the acknowledgement feature existed a rejection could not be recorded at all and
    /// the escalation kept chasing: noisy, but correct. The feature closed a double-buy and opened
    /// an un-buy, which is worse, because a duplicate order is discovered when it arrives and a
    /// missing one is discovered when the customer asks where their goods are.</para>
    ///
    /// <para>Written as an <see cref="Expression"/> so the EF query and the in-memory workbench
    /// projection share one definition rather than each restating it — the two disagreeing is
    /// exactly how the original double-buy got in. Use <c>.Where(IsCommittedSupply)</c> against
    /// <c>IQueryable</c> and <see cref="IsCommittedSupplyPredicate"/> against a materialised list.
    /// A COUNTERED order stays committed: the supplier is still supplying, on revised terms.</para>
    /// </summary>
    /// <summary>
    /// <see cref="CommittedSupply"/> flattened for SQL, and it must stay an array. EF translates
    /// <c>Contains</c> on an array into an <c>IN</c> list; it cannot translate
    /// <c>IReadOnlySet&lt;string&gt;.Contains</c>, which is an interface method rather than
    /// <c>Enumerable.Contains</c>, so writing the set directly into the expression below compiles
    /// cleanly and then throws at query time — on PostgreSQL only. Derived from the set rather than
    /// restated beside it, so a status added to one is added to both.
    /// </summary>
    private static readonly string[] CommittedSupplyForSql = CommittedSupply.ToArray();

    public static readonly Expression<Func<SupplierPurchaseOrder, bool>> IsCommittedSupply =
        order => CommittedSupplyForSql.Contains(order.Status)
            && order.AcknowledgementStatus != SupplierAcknowledgementStatuses.Rejected;

    /// <summary>Compiled form of <see cref="IsCommittedSupply"/>, for already-materialised orders.</summary>
    public static readonly Func<SupplierPurchaseOrder, bool> IsCommittedSupplyPredicate =
        IsCommittedSupply.Compile();

    /// <summary>
    /// States in which the buyer can still withdraw the order unilaterally. IN_PRODUCTION and
    /// SHIPPED are deliberately excluded: once the supplier has committed materially, withdrawing
    /// is a commercial negotiation, not a status change. Receipt of any quantity is checked
    /// separately, so this set is a precondition and not the whole rule.
    /// </summary>
    public static readonly IReadOnlySet<string> Cancellable = new HashSet<string>(StringComparer.Ordinal)
    {
        Draft, Approved, Sent, Issued, Acknowledged
    };

    public const string PartiallyReceived = "PARTIALLY_RECEIVED";
    public const string Received = "RECEIVED";

    /// <summary>Commercially finished — received and settled. Terminal.</summary>
    public const string Closed = "CLOSED";

    public const string Cancelled = "CANCELLED";
}

/// <summary>FR-SPO-03. What the supplier said when the order reached them.</summary>
/// <summary>
/// Incoterms 2020, validated as a closed set.
///
/// <para>An Incoterm is not a label. It decides who pays freight, where risk passes, and who
/// clears customs at each end. A typo or an invented code silently changes the commercial position
/// of the order and is not discovered until a shipment is sitting at a port with nobody obliged to
/// clear it — so free text is refused.</para>
/// </summary>
public static class Incoterms
{
    public static readonly IReadOnlySet<string> Codes = new HashSet<string>(StringComparer.Ordinal)
    {
        "EXW", "FCA", "FAS", "FOB", "CFR", "CIF", "CPT", "CIP", "DAP", "DPU", "DDP"
    };

    /// <summary>
    /// The D group — the terms under which the seller delivers to a named place in the buyer's
    /// country. Everything outside it (EXW, F-group, C-group) hands the goods over before import,
    /// so the BUYER pays the customs duty and it is not inside the quoted price.
    ///
    /// <para>Used to warn, never to compute. Decision R8 rules out deriving a duty cost from the
    /// Incoterm or an HS code, and this set does no arithmetic: it only lets the offer say "this is
    /// an EXW quote with no duty recorded" instead of pricing that silently.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> DeliveredCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "DAP", "DPU", "DDP"
    };

    /// <summary>
    /// True when the term is recorded, valid, and hands the goods over before import clearance —
    /// the case in which a zero duty is a gap rather than a fact. An absent Incoterm returns false:
    /// most domestic KSA supply carries no Incoterm and genuinely owes no duty, and warning on
    /// every one of those would teach buyers to ignore the warning that matters.
    /// </summary>
    public static bool IsNonDelivered(string? incoterm)
    {
        var code = incoterm?.Trim().ToUpperInvariant();
        return !string.IsNullOrEmpty(code) && Codes.Contains(code) && !DeliveredCodes.Contains(code);
    }
}

public static class SupplierAcknowledgementStatuses
{
    public const string Accepted = "ACCEPTED";
    public const string Rejected = "REJECTED";

    /// <summary>Accepted on different terms — normally a revised lead time.</summary>
    public const string Countered = "COUNTERED";
}

public static class GoodsReceiptStatuses
{
    public const string Posted = "POSTED";
}

public static class ProcurementOutboxStatuses
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Sent = "SENT";
    public const string Failed = "FAILED";
    public const string DeadLettered = "DEAD_LETTERED";
}

/// <summary>Why a supplier purchase order exists — the discriminator R4 turns on.</summary>
public static class SupplierPurchaseOrderDemandSources
{
    /// <summary>Replenishment. Has no customer behind it, and legitimately carries no customer keys.</summary>
    public const string Stock = "STOCK";

    /// <summary>Bought against a specific customer award. Must carry the customer chain below.</summary>
    public const string CustomerDemand = "CUSTOMER_DEMAND";
}

public sealed class SupplierPurchaseOrder
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long RfqId { get; set; }

    /// <summary>
    /// R4 / FR-SPO-05 / FR-COM-07. Why this order exists, and the customer chain behind it when
    /// there is one.
    ///
    /// <para>The order previously carried only <see cref="RfqId"/>, so every downstream trace —
    /// delivery, invoice, where-used — had to re-join through the RFQ, and the Sales Order was
    /// connected to procurement by nothing at all. In practice that made the RFQ the de-facto
    /// spine, the inverse of what FR-COM-07 requires.</para>
    ///
    /// <para>The discriminator is why these are nullable rather than required. A stock
    /// replenishment order genuinely has no customer, and modelling that as "missing data" would
    /// force either a fake customer or a nullable column nobody can reason about. With the
    /// discriminator, absent keys on a STOCK order are correct and absent keys on a
    /// CUSTOMER_DEMAND order are a defect.</para>
    /// </summary>
    public string DemandSource { get; set; } = SupplierPurchaseOrderDemandSources.CustomerDemand;

    public long? CustomerPurchaseOrderId { get; set; }

    public long? CustomerOrderId { get; set; }

    public long? QuoteId { get; set; }

    /// <summary>
    /// The commercial case this order belongs to, carried rather than joined.
    ///
    /// <para>It is reachable through the customer chain above, but a supplier purchase order that
    /// cannot state its own master reference forces a multi-table join to print the one identifier
    /// a buyer, a supplier and an auditor all quote back at us. Null on a STOCK order, which has
    /// no case behind it.</para>
    /// </summary>
    public long? CommercialCaseId { get; set; }

    public string? NexoraSerial { get; set; }

    public long SupplierId { get; set; }
    public long CurrencyId { get; set; }
    public string PurchaseOrderNumber { get; set; } = null!;
    public string Status { get; set; } = SupplierPurchaseOrderStatuses.Draft;
    public decimal TotalValue { get; set; }
    public DateOnly? ExpectedOn { get; set; }

    /// <summary>
    /// FR-SPO-01. Who authorised this order for release, and when.
    ///
    /// <para>An order went straight from DRAFT to ISSUED, so nothing recorded that a buyer had
    /// approved it — and because award approval and PO issuance are separate permissions but not
    /// separate people, one person could do both. Recording the approver on the order itself is the
    /// precondition for segregation of duties being checkable at all: the check compares this user
    /// against the user who approved the sourcing awards behind
    /// <see cref="SupplierPurchaseOrderLine.SourcingAwardId"/>.</para>
    ///
    /// <para>The user id is the identity the rule is enforced on; the actor string is the audited
    /// name, which a renamed or deleted user must not be able to erase.</para>
    /// </summary>
    public long? ApprovedByUserId { get; set; }

    public string? ApprovedBy { get; set; }

    public DateTime? ApprovedOn { get; set; }

    /// <summary>
    /// When the order was actually released to the supplier.
    ///
    /// <para>The overdue-acknowledgement clock has to start from dispatch, and without this it
    /// started from approval instead — generous to the supplier by however long the buyer sat on
    /// an approved order, which is exactly the delay the escalation exists to expose.</para>
    /// </summary>
    public DateTime? SentToSupplierOn { get; set; }

    /// <summary>
    /// FR-SPO-03. The supplier's answer: accepted, rejected, or countered on different terms.
    /// Null means the order is still awaiting a response, which is the state FR-SPO-07 escalates.
    /// </summary>
    public string? AcknowledgementStatus { get; set; }

    public DateTime? AcknowledgedOn { get; set; }

    public string? AcknowledgedBy { get; set; }

    /// <summary>The supplier's own words on a rejection or counter — the reason a buyer acts on.</summary>
    public string? AcknowledgementNote { get; set; }

    /// <summary>
    /// A counter-offered lead time, in days. Kept separate from the ordered lead time so the
    /// original commitment and the supplier's revision are both legible — overwriting the first
    /// with the second loses the fact that anything changed.
    /// </summary>
    public int? RevisedLeadTimeDays { get; set; }

    /// <summary>
    /// FR-SPO-07. The ship date the supplier committed to. Reminders run against this, and it is
    /// what "overdue" is measured from; without it there is nothing to be late against.
    /// </summary>
    public DateOnly? CommittedShipDate { get; set; }

    /// <summary>
    /// FR-SPO-06. Trade terms, which decide who pays for what and where risk passes. An Incoterm
    /// with no named port is only half a term, so the ports sit with it.
    /// </summary>
    public string? Incoterm { get; set; }

    public string? PortOfLoading { get; set; }

    public string? PortOfDischarge { get; set; }

    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }
    public ICollection<SupplierPurchaseOrderLine> Lines { get; set; } = new List<SupplierPurchaseOrderLine>();

    /// <summary>
    /// Carries a resolved commercial case onto this order at the moment it is created.
    ///
    /// <para>The caller resolves the case from a tenant-scoped read of the document that owns it —
    /// the RFQ, or one of the customer keys above. The tenant of that read is passed back in so the
    /// copy itself is checked: a case id is a tenant-owned key, and a supplier purchase order that
    /// quoted another business unit's master reference would leak one tenant's commercial identity
    /// into another's paperwork.</para>
    ///
    /// <para>A STOCK order is left untouched. Replenishment has no customer and therefore no case,
    /// so a null here is the correct answer rather than missing data — see
    /// <see cref="DemandSource"/>.</para>
    /// </summary>
    public void InheritCommercialIdentity(long sourceBusinessUnitId, long? commercialCaseId, string? nexoraSerial)
    {
        if (sourceBusinessUnitId != BusinessUnitId)
            throw new InvalidOperationException(
                "A commercial case cannot be carried across business units.");
        if (DemandSource == SupplierPurchaseOrderDemandSources.Stock)
            return;
        if (CommercialCaseId.HasValue && CommercialCaseId != commercialCaseId)
            throw new InvalidOperationException(
                "A supplier purchase order commercial identity cannot be replaced.");

        CommercialCaseId = commercialCaseId;
        NexoraSerial = nexoraSerial;
    }

    /// <summary>
    /// FR-COM-07. Records the customer documents this order was raised to satisfy, resolved from the
    /// governed bridge between the sourcing awards on it and the customer quote line they cover.
    ///
    /// <para>This is where the asymmetry described on <see cref="DemandSource"/> stops being a
    /// comment and becomes an invariant. On a STOCK order any customer key at all is a
    /// contradiction and is refused outright — replenishment has no customer, so a key here could
    /// only have come from a mis-wired caller. On a CUSTOMER_DEMAND order the keys are whatever the
    /// chain can prove: a key the chain cannot supply stays null and is reported as a gap by
    /// <c>CommercialCaseQueryService</c>, because a manufactured link would be quoted back at us as
    /// though it were real.</para>
    ///
    /// <para>The tenant of the reads that produced the keys is passed back in and checked. These are
    /// tenant-owned keys, and a supplier purchase order naming another business unit's customer PO
    /// would leak one tenant's commercial chain into another's paperwork.</para>
    ///
    /// <para>The origin is written once. Replacing it later would silently re-point a committed
    /// document at a different customer, so a differing value is refused rather than applied.</para>
    /// </summary>
    public void AttachCustomerOrigin(long sourceBusinessUnitId, long? customerPurchaseOrderId,
        long? customerOrderId, long? quoteId)
    {
        if (sourceBusinessUnitId != BusinessUnitId)
            throw new InvalidOperationException(
                "A customer origin cannot be carried across business units.");

        if (DemandSource == SupplierPurchaseOrderDemandSources.Stock)
        {
            if (customerPurchaseOrderId.HasValue || customerOrderId.HasValue || quoteId.HasValue)
                throw new InvalidOperationException(
                    "A STOCK replenishment purchase order has no customer behind it and cannot carry "
                    + "a customer purchase order, sales order or quotation key.");
            return;
        }

        if ((CustomerPurchaseOrderId.HasValue && CustomerPurchaseOrderId != customerPurchaseOrderId)
            || (CustomerOrderId.HasValue && CustomerOrderId != customerOrderId)
            || (QuoteId.HasValue && QuoteId != quoteId))
            throw new InvalidOperationException(
                "A supplier purchase order customer origin cannot be replaced.");

        CustomerPurchaseOrderId = customerPurchaseOrderId;
        CustomerOrderId = customerOrderId;
        QuoteId = quoteId;
    }

    /// <summary>
    /// True when the order names at least one customer document it was bought for. Absent on a
    /// STOCK order by definition; absent on a CUSTOMER_DEMAND order it is a reportable gap, never a
    /// blank to be read past.
    /// </summary>
    [NotMapped]
    public bool StatesCustomerOrigin =>
        CustomerPurchaseOrderId.HasValue || CustomerOrderId.HasValue || QuoteId.HasValue;
}

public sealed class SupplierPurchaseOrderLine
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SupplierPurchaseOrderId { get; set; }
    public long SourcingAwardId { get; set; }
    public long SupplierQuotedItemId { get; set; }
    public long RfqId { get; set; }
    public long RfqItemId { get; set; }
    public long ProductId { get; set; }
    public long WarehouseId { get; set; }
    public long? InventoryId { get; set; }
    public long? IncomingInventoryId { get; set; }
    public decimal OrderedQuantity { get; set; }
    public decimal ReceivedQuantity { get; set; }

    /// <summary>
    /// FR-MAS-05. Running total of this line's quantity placed on inbound supplier shipments that
    /// have not been cancelled.
    ///
    /// <para>Partial shipment is the normal case — one line for 500 units routinely arrives as
    /// 200 + 300 weeks apart — so the reconciliation invariant "the sum shipped can never exceed the
    /// quantity ordered" needs somewhere to live that a second code path cannot bypass. It lives
    /// here, maintained by <c>InboundShipmentApplicationService</c> inside the same serializable
    /// transaction that writes the shipment line, and guarded by
    /// <c>CK_supplier_purchase_order_lines_ShippedQuantity</c>. A validator alone is a rule that
    /// holds only until the next writer.</para>
    ///
    /// <para>Cancelling a shipment releases its quantity back here, otherwise a line abandoned in
    /// transit could never be re-shipped.</para>
    ///
    /// <para>Deliberately NOT bounded by <see cref="ReceivedQuantity"/>: a goods receipt can be
    /// posted for material that was never recorded on a shipment (a local supplier delivering by
    /// van), and forcing the two to agree would refuse a legitimate receipt.</para>
    /// </summary>
    public decimal ShippedQuantity { get; set; }

    public decimal UnitCost { get; set; }
    public decimal LandedUnitCost { get; set; }

    /// <summary>
    /// FR-SPO-06 / FR-MTR-04. Customs identity, captured per line because that is the level
    /// customs assesses. The item master carries a default, but the code and origin that actually
    /// applied to THIS purchase are what a clearance file and a certificate of origin must agree
    /// with, and the master can change afterwards.
    /// </summary>
    public string? HsCode { get; set; }

    public string? CountryOfOrigin { get; set; }

    public long Version { get; set; } = 1;
}

public sealed class GoodsReceipt
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SupplierPurchaseOrderId { get; set; }
    public long WarehouseId { get; set; }
    public string ReceiptNumber { get; set; } = null!;
    public string Status { get; set; } = GoodsReceiptStatuses.Posted;
    public DateTime ReceivedOn { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public ICollection<GoodsReceiptLine> Lines { get; set; } = new List<GoodsReceiptLine>();
}

public sealed class GoodsReceiptLine
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long GoodsReceiptId { get; set; }
    public long SupplierPurchaseOrderLineId { get; set; }
    public long ProductId { get; set; }
    public long InventoryId { get; set; }
    public long WarehouseId { get; set; }
    public long InventoryMovementId { get; set; }
    public decimal ReceivedQuantity { get; set; }
}

public sealed class ProcurementEvent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string AggregateType { get; set; } = null!;
    public long AggregateId { get; set; }
    public long AggregateVersion { get; set; }
    public string EventType { get; set; } = null!;
    public string Actor { get; set; } = null!;
    public string CorrelationId { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string PayloadJson { get; set; } = "{}";
    public DateTime OccurredOn { get; set; }
}

public sealed class ProcurementOutboxMessage
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SupplierSolicitationId { get; set; }
    public string MessageType { get; set; } = "SUPPLIER_RFQ";
    public string Status { get; set; } = ProcurementOutboxStatuses.Pending;
    public string PayloadJson { get; set; } = "{}";
    public int AttemptCount { get; set; }

    /// <summary>
    /// Which delivery round this row is on. Zero for the original dispatch; incremented by
    /// every surface that resets <see cref="AttemptCount"/> (the manual retry and platform
    /// dead-letter recovery). Attempt numbers repeat across rounds, so the round — not the
    /// attempt — is what makes a redelivery's ledger events distinct facts from the first
    /// round's rather than duplicate keys.
    /// </summary>
    public int DispatchRound { get; set; }

    public DateTime NextAttemptOn { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public DateTime? DeadLetteredOn { get; set; }
    public DateTime? SentOn { get; set; }
    public string? ProviderReference { get; set; }
    public string? ProviderName { get; set; }
    public string? OriginCorrelationId { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime UpdatedOn { get; set; }
}
