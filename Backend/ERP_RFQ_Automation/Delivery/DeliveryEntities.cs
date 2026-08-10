namespace ERP_RFQ_Automation.Delivery;

/// <summary>
/// Gate 7 / Module 7 — FR-DLM-05. The governed outbound delivery lifecycle.
///
/// <para><b>Why this exists.</b> The outbound <c>Shipment</c> has always carried its state in
/// <c>StatusId</c>, a foreign key into <c>SetupMaster</c> where <c>SetupType = 'ShipmentStatus'</c>
/// — an <b>unseeded, tenant-authored picklist</b>. Register item E20 recorded the consequence for
/// the inbound side and Gate 5 fixed it there; the outbound side was left as it was. Every tenant
/// invents its own words, nothing constrains them, and the only consumer in the product was a
/// colour lookup in <c>ShipmentViewPage.tsx</c> that pattern-matched on the strings
/// <c>'DELIVERED'</c>, <c>'SHIPPED'</c>, <c>'IN TRANSIT'</c> and <c>'PENDING'</c> and fell through
/// to grey for anything else.</para>
///
/// <para>That is why FR-DLM-02 could not be built. "Cumulative delivered" is meaningless until
/// "delivered" is a value the database recognises rather than a word an operator typed. The BRD
/// hands us the exact set in FR-DLM-05 — Scheduled, Dispatched, In Transit, Delivered, Delivery
/// Exception — so FR-DLM-05 is not a sibling of FR-DLM-02, it is its prerequisite.</para>
///
/// <para><b>Status is derived at confirmation, never asserted.</b> See
/// <see cref="DeliveryStatuses.DeliveryException"/>: the operator records what the customer
/// actually accepted, line by line, and the status follows from the numbers. An operator cannot
/// mark a shipment Delivered while recording a shortfall, and cannot mark it an Exception while
/// recording full acceptance, because neither is a thing they are asked.</para>
/// </summary>
public static class DeliveryStatuses
{
    /// <summary>
    /// Planned, not yet gone. Nothing has left the building, so this accrues no despatched
    /// quantity and — critically — <b>issues no stock</b>. See
    /// <c>ShipmentController.IssueOrderStockAsync</c>: the goods issue and the despatch accrual are
    /// deliberately driven from the same call site, because they mean the same physical fact and
    /// two writers for one fact is how they drift.
    /// </summary>
    public const string Scheduled = "SCHEDULED";

    /// <summary>Goods have left. This is the accrual point for despatched quantity and stock.</summary>
    public const string Dispatched = "DISPATCHED";

    public const string InTransit = "IN_TRANSIT";

    /// <summary>
    /// The customer confirmed receipt of everything on the manifest. Reached only through
    /// <c>ConfirmDeliveryAsync</c>, never by an operator selecting it from a list.
    /// </summary>
    public const string Delivered = "DELIVERED";

    /// <summary>
    /// FR-DLM-07. The customer confirmed receipt of <b>less</b> than the manifest — short, damaged
    /// or rejected. Also reached only through confirmation, and only because the recorded accepted
    /// quantity was below the despatched quantity on at least one line.
    ///
    /// <para>This is not "worse than Delivered", it is <i>Delivered with a discrepancy</i>. The
    /// accepted units on an exception shipment count towards cumulative delivered exactly as they
    /// would on a clean one; the shortfall stays outstanding on the order line. Treating the whole
    /// consignment as undelivered because two of ten cartons were damaged would leave eight
    /// invoiceable units invisible.</para>
    /// </summary>
    public const string DeliveryException = "DELIVERY_EXCEPTION";

    /// <summary>
    /// Abandoned. Reverses whatever it accrued, which is why it cannot be reached from
    /// <see cref="Delivered"/> or <see cref="DeliveryException"/> — see <see cref="CanTransition"/>.
    /// </summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>
    /// The forward-only spine. Skips are legal: a same-city van run goes straight from
    /// <see cref="Dispatched"/> to confirmation without anyone keying IN_TRANSIT, and demanding
    /// every rung would force operators to record milestones that did not happen — the Gate 5
    /// milestone ladder made the same call for the same reason.
    /// </summary>
    public static readonly IReadOnlyList<string> Ladder =
        [Scheduled, Dispatched, InTransit, Delivered];

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Scheduled, Dispatched, InTransit, Delivered, DeliveryException, Cancelled
    };

    /// <summary>Nothing leaves these. Both are outcomes, not waypoints.</summary>
    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        Delivered, Cancelled
    };

    /// <summary>
    /// States in which the goods are physically out of the warehouse and therefore counted in
    /// <c>OrderItem.DespatchedQuantity</c>.
    ///
    /// <para>Kept here, next to the constants, rather than written inline at each guard. Wiring
    /// contract failure #9 and decision R13: adding <c>ACKNOWLEDGED</c> to the supplier PO broke
    /// goods receipt and cancellation precisely because every guard carried its own hand-written
    /// status list. The next status added to this ladder must be a visible decision in this file,
    /// not a clause somebody forgets in one method out of four.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> Despatched = new HashSet<string>(StringComparer.Ordinal)
    {
        Dispatched, InTransit, Delivered, DeliveryException
    };

    /// <summary>
    /// States a delivery confirmation may be recorded against. A SCHEDULED shipment cannot be
    /// confirmed received, because nothing was sent.
    /// </summary>
    public static readonly IReadOnlySet<string> Confirmable = new HashSet<string>(StringComparer.Ordinal)
    {
        Dispatched, InTransit
    };

    /// <summary>
    /// States a shipment may still be cancelled from. Deliberately excludes both confirmed
    /// outcomes: once a customer has signed for goods, "cancellation" is a return or a credit
    /// note, which is a commercial transaction with its own evidence — not a status change that
    /// silently un-accrues delivered quantity from an order line an invoice may already reference.
    /// Gate 5 rejected the identical shortcut inbound.
    /// </summary>
    public static readonly IReadOnlySet<string> Cancellable = new HashSet<string>(StringComparer.Ordinal)
    {
        Scheduled, Dispatched, InTransit
    };

    public static int Rank(string status)
    {
        for (var i = 0; i < Ladder.Count; i++)
            if (string.Equals(Ladder[i], status, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>
    /// Forward-only along the ladder, plus cancellation from anything still in flight.
    ///
    /// <para><see cref="Delivered"/> and <see cref="DeliveryException"/> are absent as explicit
    /// targets on purpose: they are outcomes of recording what the customer accepted, and are
    /// applied by the confirmation path rather than selected. An operator route into them would
    /// let someone stamp DELIVERED with no accepted quantities behind it, which is exactly the
    /// number FR-DLM-02 exists to make trustworthy.</para>
    /// </summary>
    public static bool CanTransition(string current, string next)
    {
        if (!All.Contains(current) || !All.Contains(next)) return false;
        if (Terminal.Contains(current)) return false;
        if (string.Equals(current, next, StringComparison.Ordinal)) return false;

        if (string.Equals(next, Cancelled, StringComparison.Ordinal))
            return Cancellable.Contains(current);

        // An exception shipment is already confirmed; the only forward move is a re-confirmation,
        // which the confirmation path handles rather than this ladder.
        if (string.Equals(current, DeliveryException, StringComparison.Ordinal)) return false;

        if (string.Equals(next, Delivered, StringComparison.Ordinal)
            || string.Equals(next, DeliveryException, StringComparison.Ordinal))
            return false;

        var from = Rank(current);
        var to = Rank(next);
        return from >= 0 && to > from;
    }
}

/// <summary>
/// FR-DLM-07. Why a receiving bay accepted fewer units than the manifest declared.
///
/// <para>A code rather than free text, for the reason every controlled vocabulary in this codebase
/// exists: a corrective-action queue is a query, and "damaged" / "Damaged" / "box was crushed" are
/// three different values to a <c>GROUP BY</c>. The operator's own words are still captured
/// alongside it — the code says which workflow, the note says what happened.</para>
/// </summary>
public static class DeliveryExceptionReasons
{
    /// <summary>Fewer units arrived than the note declared. A quantity dispute with the carrier.</summary>
    public const string ShortShipment = "SHORT_SHIPMENT";

    /// <summary>Units arrived and were visibly damaged. A claim, and usually a replacement.</summary>
    public const string Damaged = "DAMAGED";

    /// <summary>Units arrived intact and the customer refused them — wrong item, wrong spec, late.</summary>
    public const string Rejected = "REJECTED";

    /// <summary>Units never arrived and the carrier cannot account for them.</summary>
    public const string LostInTransit = "LOST_IN_TRANSIT";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ShortShipment, Damaged, Rejected, LostInTransit
    };
}

/// <summary>
/// FR-DLM-03. Proof of delivery: who signed, what they signed, where and when.
///
/// <para>One row per confirmation event, written in the same transaction as the accepted
/// quantities it evidences. That coupling is the point — a delivered quantity with no POD behind
/// it, or a POD with no quantities, is the half-record that makes a dispute unwinnable.</para>
///
/// <para><b>Every evidential field is nullable and every absence is visible.</b> A driver with no
/// signal cannot capture GPS; a customer's storeman may refuse a photograph. Refusing the
/// confirmation in those cases would push operators to record the delivery nowhere at all, and a
/// missing POD field is recoverable where a missing delivery is not. What is <i>not</i> optional is
/// the receiving contact's name: an unsigned delivery note is not proof of anything.</para>
/// </summary>
public sealed class DeliveryProof
{
    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    public long ShipmentId { get; set; }

    /// <summary>Who took receipt. Required — this is the person the delivery is proved against.</summary>
    public string ReceivedByName { get; set; } = null!;

    /// <summary>Their phone, extension or email. Absent is honest; invented is not.</summary>
    public string? ReceivedByContact { get; set; }

    /// <summary>Their role or department at the delivery point, where the driver captured it.</summary>
    public string? ReceivedByPosition { get; set; }

    /// <summary>
    /// The moment the goods changed hands, as opposed to the moment somebody keyed it in
    /// (<see cref="RecordedOn"/>). These differ whenever a driver confirms a round in the evening,
    /// and a dispute turns on the first one.
    /// </summary>
    public DateTime ReceivedOn { get; set; }

    /// <summary>
    /// Content-addressed evidence ids in the existing immutable evidence store — the same store
    /// Gate 5 puts lot certificates in. Null means not captured, and the UI says so rather than
    /// showing an empty frame that reads like a failed image load.
    /// </summary>
    public long? SignatureEvidenceId { get; set; }

    public long? StampEvidenceId { get; set; }

    public long? PhotoEvidenceId { get; set; }

    /// <summary>
    /// WGS-84, as reported by the capturing device. Stored to 7 decimal places (~1cm), which is far
    /// beyond what a phone can actually resolve — hence <see cref="GpsAccuracyMeters"/>, without
    /// which a coordinate is a claim with no error bar and reads as more precise than it is.
    /// </summary>
    public decimal? GpsLatitude { get; set; }

    public decimal? GpsLongitude { get; set; }

    public decimal? GpsAccuracyMeters { get; set; }

    /// <summary>
    /// The device clock at the moment of the fix. Distinct from <see cref="ReceivedOn"/> because
    /// they can legitimately disagree, and a GPS timestamp that silently inherits a keyed-in time
    /// is evidence of nothing.
    /// </summary>
    public DateTime? GpsCapturedOn { get; set; }

    public string? Notes { get; set; }

    /// <summary>Copied from the shipment so the proof states its own case, as the note does.</summary>
    public long? CommercialCaseId { get; set; }

    public string? NexoraSerial { get; set; }

    public string IdempotencyKey { get; set; } = null!;

    public string RecordedBy { get; set; } = null!;

    public DateTime RecordedOn { get; set; }

    public long Version { get; set; } = 1;

    /// <summary>
    /// True when a coordinate pair was captured. Used by the read model to distinguish "no fix"
    /// from "a fix at the equator", which <c>0,0</c> would otherwise conflate.
    /// </summary>
    public bool HasGpsFix => GpsLatitude.HasValue && GpsLongitude.HasValue;
}
