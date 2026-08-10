namespace ERP_RFQ_Automation.InboundLogistics;

/// <summary>
/// FR-MAS-01. The six inbound milestones, as an ORDERED lifecycle rather than a free picklist.
///
/// <para>E20 recorded the defect this replaces: outbound <c>Shipment.StatusId</c> points at an
/// unseeded <c>SetupMaster</c> picklist, so every tenant invented its own words and no cross-tenant
/// KPI, delay rule or audit-grade milestone evidence was possible. These constants are the
/// server-enforced vocabulary; the database CHECK constraint refuses anything else.</para>
///
/// <para><b>Forward-only, skips allowed.</b> <see cref="Rank"/> gives the ladder its order and a
/// transition must strictly increase it. Skipping is legitimate and deliberate: a shipment from a
/// Riyadh supplier to a Riyadh warehouse never touches a port and never clears customs, so
/// demanding every rung would force operators to record milestones that did not happen. What is
/// refused is going BACKWARDS, repeating a rung, or moving out of a terminal state — those are the
/// transitions that would rewrite history or resurrect a closed shipment.</para>
/// </summary>
public static class InboundShipmentMilestones
{
    public const string ReadyAtFactory = "READY_AT_FACTORY";
    public const string DepartedOrigin = "DEPARTED_ORIGIN";
    public const string InTransit = "IN_TRANSIT";
    public const string ArrivedSaudiPort = "ARRIVED_SAUDI_PORT";
    public const string CustomsClearance = "CUSTOMS_CLEARANCE";
    public const string ReceivedAtWarehouse = "RECEIVED_AT_WAREHOUSE";

    /// <summary>
    /// Not one of the six. A shipment that will never arrive — cancelled by the supplier, lost, or
    /// keyed in error — has to be representable, otherwise its quantity stays reserved against the
    /// purchase order line forever and the line can never be re-shipped.
    /// </summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>The ladder in order. Index is the rank; a transition must strictly increase it.</summary>
    public static readonly IReadOnlyList<string> Ladder =
    [
        ReadyAtFactory, DepartedOrigin, InTransit, ArrivedSaudiPort, CustomsClearance, ReceivedAtWarehouse
    ];

    /// <summary>Every legal value of the column, including the terminal cancellation.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(Ladder.Append(Cancelled), StringComparer.Ordinal);

    /// <summary>Nothing may follow these. RECEIVED is the end of the inbound journey; CANCELLED is a
    /// shipment that no longer exists commercially.</summary>
    public static readonly IReadOnlySet<string> Terminal =
        new HashSet<string>(StringComparer.Ordinal) { ReceivedAtWarehouse, Cancelled };

    /// <summary>Position in the ladder, or -1 for <see cref="Cancelled"/>, which sits outside it.</summary>
    public static int Rank(string milestone)
    {
        for (var index = 0; index < Ladder.Count; index++)
            if (string.Equals(Ladder[index], milestone, StringComparison.Ordinal))
                return index;
        return -1;
    }

    /// <summary>
    /// Whether <paramref name="next"/> may follow <paramref name="current"/>.
    ///
    /// <para>Cancellation is reachable from any non-terminal state, because a shipment can be
    /// abandoned at any point before it lands. Everything else must move strictly forward.</para>
    /// </summary>
    public static bool CanTransition(string current, string next)
    {
        if (!All.Contains(next) || Terminal.Contains(current)) return false;
        if (string.Equals(next, Cancelled, StringComparison.Ordinal)) return true;
        return Rank(next) > Rank(current);
    }
}

/// <summary>
/// FR-MAS-02. Where a shipment's status and ETA came from.
///
/// <para>Only <see cref="Manual"/> is reachable today. <see cref="CarrierApi"/> exists as the named
/// seam so that when a carrier integration is built it does not have to retro-fit provenance onto
/// rows that never recorded it — but nothing in this codebase writes it, and D4 (the Phase 1 carrier
/// subset) is still open. See the gate report: the API half of FR-MAS-02 is NOT built.</para>
/// </summary>
public static class InboundShipmentTrackingSources
{
    public const string Manual = "MANUAL";
    public const string CarrierApi = "CARRIER_API";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Manual, CarrierApi };
}

/// <summary>What kind of entry point a <see cref="PortOfEntry"/> is. Reports group by this.</summary>
public static class PortOfEntryKinds
{
    public const string Seaport = "SEAPORT";
    public const string Airport = "AIRPORT";
    public const string DryPort = "DRY_PORT";
    public const string LandBorder = "LAND_BORDER";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Seaport, Airport, DryPort, LandBorder };
}

/// <summary>
/// FR-MAS-01 / E21. A named entry point, held as governed reference data rather than free text.
///
/// <para>Free text cannot be grouped. "Jeddah", "Jeddah Islamic", "JED" and "jeddah islamic port"
/// are four rows in any report built on a string column, which is why the BRD asks for named
/// options and why E21 asks whether the list is a governed master-data type. It is: tenant-owned,
/// coded, deactivatable, and referenced by foreign key from the shipment.</para>
///
/// <para><b>E21 remains formally open</b> on who supplies the controlled list. This entity does not
/// pre-empt that: a tenant may create its own rows, and the canonical Saudi entry points in
/// <see cref="SaudiPortCatalogue"/> are loaded only when an authenticated operator asks for them.
/// Nothing is auto-seeded into a tenant.</para>
/// </summary>
public sealed class PortOfEntry
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>Stable, tenant-unique code. Uppercased on write so two spellings cannot coexist.</summary>
    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>One of <see cref="PortOfEntryKinds"/>.</summary>
    public string Kind { get; set; } = PortOfEntryKinds.Seaport;

    /// <summary>ISO 3166-1 alpha-2. Defaults to SA, but the column is not restricted to it — a GCC
    /// tenant clearing through Jebel Ali before a land move into the Kingdom needs to name it.</summary>
    public string CountryCode { get; set; } = "SA";

    public string? City { get; set; }

    /// <summary>Deactivated rather than deleted: shipments already reference it.</summary>
    public bool IsActive { get; set; } = true;

    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }
}

/// <summary>
/// The canonical Saudi entry points, offered as an explicit one-click load rather than seeded.
///
/// <para><b>Why this is not seed data.</b> Nothing writes these rows unless an authenticated
/// operator calls the import endpoint for their own tenant. They are verifiable real-world
/// reference facts that FR-MAS-01 names directly ("including named Saudi port/location options"),
/// not demo records, and every one is editable and deactivatable afterwards. The alternative — an
/// empty dropdown on day one — makes the requirement unusable without a developer, which is exactly
/// what this gate is required to avoid.</para>
///
/// <para>The codes are Nexora codes, deliberately NOT presented as UN/LOCODEs. Asserting a LOCODE
/// we have not verified would be inventing data of exactly the kind a customs file depends on.</para>
/// </summary>
public static class SaudiPortCatalogue
{
    public readonly record struct Entry(string Code, string Name, string Kind, string City);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new("JEDDAH-ISLAMIC", "Jeddah Islamic Port", PortOfEntryKinds.Seaport, "Jeddah"),
        new("DAMMAM-KAP", "King Abdulaziz Port, Dammam", PortOfEntryKinds.Seaport, "Dammam"),
        new("JUBAIL-COMMERCIAL", "Jubail Commercial Port", PortOfEntryKinds.Seaport, "Jubail"),
        new("KING-ABDULLAH-PORT", "King Abdullah Port", PortOfEntryKinds.Seaport, "King Abdullah Economic City"),
        new("YANBU-COMMERCIAL", "Yanbu Commercial Port", PortOfEntryKinds.Seaport, "Yanbu"),
        new("JAZAN", "Jazan Port", PortOfEntryKinds.Seaport, "Jazan"),
        new("DUBA", "Duba Port", PortOfEntryKinds.Seaport, "Duba"),
        new("RIYADH-KKIA", "King Khalid International Airport", PortOfEntryKinds.Airport, "Riyadh"),
        new("JEDDAH-KAIA", "King Abdulaziz International Airport", PortOfEntryKinds.Airport, "Jeddah"),
        new("DAMMAM-KFIA", "King Fahd International Airport", PortOfEntryKinds.Airport, "Dammam"),
        new("RIYADH-DRY-PORT", "Riyadh Dry Port", PortOfEntryKinds.DryPort, "Riyadh"),
        new("BATHA", "Al Batha Land Port", PortOfEntryKinds.LandBorder, "Al Batha"),
        new("SALWA", "Salwa Land Port", PortOfEntryKinds.LandBorder, "Salwa"),
        new("KHAFJI", "Al Khafji Land Port", PortOfEntryKinds.LandBorder, "Al Khafji"),
        new("HADITHA", "Al Haditha Land Port", PortOfEntryKinds.LandBorder, "Al Haditha"),
        new("KING-FAHD-CAUSEWAY", "King Fahd Causeway", PortOfEntryKinds.LandBorder, "Al Khobar"),
    ];
}

/// <summary>
/// FR-MAS-03 / E22. The per-business-unit lead times the Material Available Date is built from.
///
/// <para>Modelled on <c>CommercialMatchingPolicy</c>: one row per business unit, created on the
/// first write, absent means <see cref="DefaultFor"/>. Two numbers, both customer-settable, no
/// engine.</para>
///
/// <para><b>Both are nullable, and null means NOT CONFIGURED — not zero.</b> This is the R17 rule
/// applied to a different column, and the reason is the same one Gate 4 learned the hard way with
/// the SLA backfill: a lead time defaulted to 0 does not fail, it silently produces a Material
/// Available Date equal to the ETA. That is a promise the warehouse cannot keep, made confidently,
/// on every shipment, with nothing on screen to say it was never configured. A null derives no date
/// at all and says why. Zero is a different, positive assertion — "clearance really is same-day" —
/// and is spelled 0.</para>
///
/// <para><b>Scope is the business unit only.</b> E22 asks whether these belong per port, per
/// supplier or per item; that question is still open and this row does not answer it. A per-port
/// override is the obvious next scope and is a nullable column on <see cref="PortOfEntry"/> when it
/// is decided — deliberately not guessed here.</para>
/// </summary>
public sealed class InboundLogisticsPolicy
{
    public static InboundLogisticsPolicy DefaultFor(long businessUnitId) =>
        new() { BusinessUnitId = businessUnitId };

    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>
    /// WORKING days between a shipment reaching a Saudi entry point and clearing customs. Null means
    /// nobody has stated one, and the Material Available Date is then not derived at all.
    /// </summary>
    public int? CustomsClearanceLeadDays { get; set; }

    /// <summary>
    /// WORKING days between goods reaching the warehouse and being available to promise. Null means
    /// not configured, with the same consequence.
    /// </summary>
    public int? PutawayLeadDays { get; set; }

    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }

    public bool IsConfigured => CustomsClearanceLeadDays.HasValue && PutawayLeadDays.HasValue;
}

/// <summary>
/// R3 / FR-MAS-01..05. An INBOUND supplier shipment, keyed to the Supplier PO.
///
/// <para>This is the aggregate decision R3 ratified. <c>Models.Shipment</c> is the OUTBOUND customer
/// despatch keyed to the sales <c>Order</c>; it is untouched by this gate. Inbound supply from a
/// supplier to a Saudi port and onward to a warehouse is a different journey with different
/// milestones, a different counterparty and a different owner, and modelling it on the outbound
/// entity would have produced one table meaning two things.</para>
///
/// <para><b>Relationship to <c>IncomingInventory</c> — this aggregate DRIVES it, it does not
/// duplicate it.</b> <c>IncomingInventory</c> is created one row per purchase order line when the
/// order is issued, and it is the quantity commitment ATP and goods receipt already read. This
/// shipment is the authority on inbound LOGISTICS — where the goods physically are, when they are
/// expected, and therefore when they become available. On every milestone and ETA change the
/// shipment recomputes <see cref="MaterialAvailableDate"/> and pushes it onto the covered lines'
/// <c>IncomingInventory.ExpectedOn</c>, and maps the milestone onto its coarse status. Nothing else
/// writes those two fields for a line that has a shipment. Superseding <c>IncomingInventory</c>
/// outright was rejected: it is wired into ATP, the sourcing net-requirement calculation and goods
/// receipt, all of which belong to other gates, and replacing it would have been a rewrite of code
/// this gate does not own. Leaving the two side by side, each answering "when does it land", is the
/// duplication this project has already paid for twice.</para>
/// </summary>
public sealed class SupplierShipment
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>R3. The key the whole aggregate hangs from.</summary>
    public long SupplierPurchaseOrderId { get; set; }

    /// <summary>Derived from the purchase order number plus a per-order sequence: PO-1234-S2.</summary>
    public string ShipmentNumber { get; set; } = null!;

    /// <summary>Current milestone. One of <see cref="InboundShipmentMilestones"/>.</summary>
    public string Milestone { get; set; } = InboundShipmentMilestones.ReadyAtFactory;

    /// <summary>The date the CURRENT milestone happened. Every earlier one keeps its own column.</summary>
    public DateOnly MilestoneOccurredOn { get; set; }

    // The six FR-MAS-01 milestone dates, each written exactly once by the transition that reaches
    // it. They are a projection of the append-only ProcurementEvent log, kept on the header so a
    // milestone report is one indexed table scan rather than a JSON dig through an event stream.
    public DateOnly? ReadyAtFactoryOn { get; set; }
    public DateOnly? DepartedOriginOn { get; set; }
    public DateOnly? InTransitOn { get; set; }
    public DateOnly? ArrivedSaudiPortOn { get; set; }
    public DateOnly? CustomsClearanceOn { get; set; }
    public DateOnly? ReceivedAtWarehouseOn { get; set; }

    /// <summary>Set when the shipment is cancelled; null otherwise.</summary>
    public DateOnly? CancelledOn { get; set; }

    public string? CancellationReason { get; set; }

    /// <summary>FR-MAS-01. The named entry point. Required by the time ARRIVED_SAUDI_PORT is recorded.</summary>
    public long? PortOfEntryId { get; set; }

    // ---- FR-MAS-02 carrier / freight forwarder ----

    public string? CarrierName { get; set; }

    /// <summary>The forwarder's own reference — bill of lading, air waybill, container number.</summary>
    public string? TrackingReference { get; set; }

    /// <summary>MANUAL today. See <see cref="InboundShipmentTrackingSources"/>.</summary>
    public string TrackingSource { get; set; } = InboundShipmentTrackingSources.Manual;

    /// <summary>The carrier's estimated arrival at the Saudi entry point.</summary>
    public DateOnly? EtaDate { get; set; }

    public DateTime? EtaUpdatedOn { get; set; }

    public string? EtaUpdatedBy { get; set; }

    // ---- FR-MAS-03 derived material available date, with its inputs ----

    /// <summary>
    /// DERIVED. Null when it cannot be derived, and <see cref="MaterialAvailableUnavailableReason"/>
    /// then says why. Never guessed.
    /// </summary>
    public DateOnly? MaterialAvailableDate { get; set; }

    /// <summary>Which fact the calculation started from: ETA, ARRIVED_SAUDI_PORT, CUSTOMS_CLEARANCE
    /// or RECEIVED_AT_WAREHOUSE.</summary>
    public string? MaterialAvailableBasisKind { get; set; }

    /// <summary>The date that basis carried, so the arithmetic is checkable without re-deriving it.</summary>
    public DateOnly? MaterialAvailableBasisDate { get; set; }

    /// <summary>Working days of customs clearance actually applied. Zero once customs is cleared.</summary>
    public int? AppliedCustomsClearanceDays { get; set; }

    /// <summary>Working days of putaway actually applied.</summary>
    public int? AppliedPutawayDays { get; set; }

    public DateTime? MaterialAvailableComputedOn { get; set; }

    /// <summary>Why no date exists — no ETA yet, lead times not configured, shipment cancelled.</summary>
    public string? MaterialAvailableUnavailableReason { get; set; }

    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }

    public ICollection<SupplierShipmentLine> Lines { get; set; } = new List<SupplierShipmentLine>();

    /// <summary>True once the shipment can no longer move.</summary>
    public bool IsTerminal => InboundShipmentMilestones.Terminal.Contains(Milestone);

    /// <summary>Total quantity on the shipment's manifest.</summary>
    public decimal ShippedQuantity => Lines.Sum(x => x.ShippedQuantity);

    /// <summary>Total quantity a goods receipt has actually counted in.</summary>
    public decimal ReceiptedQuantity => Lines.Sum(x => x.ReceivedQuantity);

    /// <summary>
    /// What is still owed a goods receipt. A shipment sitting at RECEIVED_AT_WAREHOUSE with this
    /// above zero is the honest state the earlier build hid: the goods are at the warehouse and
    /// nobody has booked them in.
    /// </summary>
    public decimal OutstandingReceiptQuantity => Lines.Sum(x => x.OutstandingReceiptQuantity);

    /// <summary>One of <see cref="InboundShipmentReceiptStates"/>, derived from the lines.</summary>
    public string ReceiptState
    {
        get
        {
            if (string.Equals(Milestone, InboundShipmentMilestones.Cancelled, StringComparison.Ordinal))
                return InboundShipmentReceiptStates.NotApplicable;
            var receipted = ReceiptedQuantity;
            if (receipted <= 0m) return InboundShipmentReceiptStates.NotReceipted;
            return receipted >= ShippedQuantity
                ? InboundShipmentReceiptStates.Receipted
                : InboundShipmentReceiptStates.PartiallyReceipted;
        }
    }

    /// <summary>Stamps the date column belonging to <paramref name="milestone"/>.</summary>
    public void StampMilestoneDate(string milestone, DateOnly occurredOn)
    {
        switch (milestone)
        {
            case InboundShipmentMilestones.ReadyAtFactory: ReadyAtFactoryOn = occurredOn; break;
            case InboundShipmentMilestones.DepartedOrigin: DepartedOriginOn = occurredOn; break;
            case InboundShipmentMilestones.InTransit: InTransitOn = occurredOn; break;
            case InboundShipmentMilestones.ArrivedSaudiPort: ArrivedSaudiPortOn = occurredOn; break;
            case InboundShipmentMilestones.CustomsClearance: CustomsClearanceOn = occurredOn; break;
            case InboundShipmentMilestones.ReceivedAtWarehouse: ReceivedAtWarehouseOn = occurredOn; break;
            case InboundShipmentMilestones.Cancelled: CancelledOn = occurredOn; break;
        }
    }

    /// <summary>
    /// The latest milestone date already recorded. A new milestone may not predate it — a shipment
    /// cannot clear customs before it arrived.
    /// </summary>
    public DateOnly? LatestRecordedMilestoneDate()
    {
        DateOnly? latest = null;
        foreach (var candidate in new[]
                 {
                     ReadyAtFactoryOn, DepartedOriginOn, InTransitOn,
                     ArrivedSaudiPortOn, CustomsClearanceOn, ReceivedAtWarehouseOn
                 })
        {
            if (candidate is { } value && (latest is null || value > latest)) latest = value;
        }
        return latest;
    }
}

/// <summary>
/// FR-MAS-05. One purchase order line's quantity on one shipment.
///
/// <para>Partial shipment is the normal case here, not an exception: a single PO line for 500 units
/// routinely arrives as 200 + 300 on two vessels weeks apart. The reconciliation invariant — the sum
/// shipped across a line's non-cancelled shipments can never exceed the quantity ordered — is
/// enforced in three places on purpose. The service checks it inside a serializable transaction; the
/// running total is maintained on <c>SupplierPurchaseOrderLine.ShippedQuantity</c> in that same
/// transaction; and a database CHECK constraint on that column refuses the write outright. A
/// validator alone is a rule that holds until someone writes a second code path.</para>
///
/// <para><b>Lot is NOT modelled here.</b> FR-MAS-05 asks for availability per shipment AND per lot.
/// No lot/batch/serial entity exists anywhere in this codebase and <c>MaterialLot</c> belongs to the
/// Gate 5 traceability agent, so this line carries no lot reference and none is faked. The seam is a
/// nullable <c>MaterialLotId</c> column with a composite (BusinessUnitId, MaterialLotId) foreign key
/// on this table — see the gate report.</para>
/// </summary>
public sealed class SupplierShipmentLine
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SupplierShipmentId { get; set; }
    public long SupplierPurchaseOrderLineId { get; set; }

    /// <summary>Carried from the purchase order line so a shipment manifest names its own product.</summary>
    public long ProductId { get; set; }

    public decimal ShippedQuantity { get; set; }

    /// <summary>
    /// FR-MAS-05 / the Gate 4–Gate 5 seam. How much of <see cref="ShippedQuantity"/> a goods receipt
    /// has actually taken in.
    ///
    /// <para>Written ONLY by <c>InboundShipmentReceiptSettlement</c>, from inside
    /// <c>ProcurementApplicationService.PostGoodsReceiptAsync</c>'s own transaction. Recording the
    /// RECEIVED_AT_WAREHOUSE milestone does not touch it, deliberately: the milestone says a truck
    /// reached the gate, the receipt says somebody counted what was on it. Keeping them apart is why
    /// this column can show "8 shipped, 0 receipted" instead of a milestone quietly implying a
    /// receipt that never happened.</para>
    ///
    /// <para>Zero is the correct value for a brand-new shipment line and for every row that exists
    /// when the table is first created, so there is no backfill hazard here — the table does not
    /// exist in PostgreSQL yet and every row it will ever hold is written by this build.</para>
    /// </summary>
    public decimal ReceivedQuantity { get; set; }

    /// <summary>What is on this shipment line and still not counted in by a goods receipt.</summary>
    public decimal OutstandingReceiptQuantity => Math.Max(0m, ShippedQuantity - ReceivedQuantity);

    public long Version { get; set; } = 1;
}

/// <summary>
/// Where one shipment stands against the goods receipts posted for it.
///
/// <para>Derived from the lines rather than stored, so it cannot drift from the quantities it
/// summarises — the failure the commercial-case reader was rebuilt to avoid.</para>
/// </summary>
public static class InboundShipmentReceiptStates
{
    /// <summary>Nothing on this shipment has been receipted.</summary>
    public const string NotReceipted = "NOT_RECEIPTED";

    /// <summary>Some quantity has been receipted and some has not.</summary>
    public const string PartiallyReceipted = "PARTIALLY_RECEIPTED";

    /// <summary>Every shipped unit has been counted in by a goods receipt.</summary>
    public const string Receipted = "RECEIPTED";

    /// <summary>A cancelled shipment holds no quantity, so there is nothing to receipt.</summary>
    public const string NotApplicable = "NOT_APPLICABLE";
}
