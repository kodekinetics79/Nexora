namespace ERP_RFQ_Automation.Traceability;

/// <summary>
/// How the material this lot holds is identified. FR-MTR-01 names lot, batch AND serial, and a
/// real trading business carries all three at once — reels of cable, cartons of fittings and
/// serialised instruments sit in the same warehouse.
///
/// <para><b>A serial is a lot of quantity one.</b> That is the whole model. Five serialised units
/// arrive as five lots of one, each with its own certificates, its own quarantine state and its own
/// where-used trace — which is exactly right, because a recall on one instrument must not quarantine
/// the other four. It also means no second table, no parallel trace path and no second quarantine
/// control to keep in step with this one.</para>
///
/// <para><b>Untracked material still gets a lot.</b> Nobody types a batch number for bulk cable, so
/// <see cref="Untracked"/> generates the identifier from the receipt. The operator is asked for
/// nothing, and the material is still traceable to its supplier purchase order, its country of
/// origin and its certificates — which is what FR-MTR-03 and FR-MTR-04 actually require. The
/// alternative, "no lot row for untracked items", would mean a goods receipt could produce stock
/// that no trace can reach, which is the one outcome this module exists to prevent.</para>
/// </summary>
public static class MaterialLotTrackingModes
{
    /// <summary>Supplier batch/lot number supplied by the operator. Quantity may be any amount.</summary>
    public const string Lot = "LOT";

    /// <summary>One physical unit, one serial number. Quantity is always exactly 1.</summary>
    public const string Serial = "SERIAL";

    /// <summary>No human-supplied identifier; the lot number is derived from the receipt.</summary>
    public const string Untracked = "UNTRACKED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Lot, Serial, Untracked
    };

    /// <summary>
    /// Resolves the mode from the item master's existing tracking switches.
    ///
    /// <para><c>Product.SerialTracking</c> and <c>Product.BatchTracking</c> have existed as booleans
    /// with nothing behind them (register item E16: "the toggle exists, the tracking does not").
    /// This is their first reader. Serial wins when both are set, because it is the stricter of the
    /// two and silently downgrading a serialised item to batch tracking loses unit identity that
    /// cannot be reconstructed afterwards.</para>
    /// </summary>
    public static string FromProduct(bool? serialTracking, bool? batchTracking)
        => serialTracking == true ? Serial : batchTracking == true ? Lot : Untracked;
}

/// <summary>
/// FR-MTR-05. A lot is either usable or held. There is no third state, and no "advisory" flag —
/// <see cref="Quarantined"/> is enforced by the stock ledger and by the fulfilment declaration, not
/// by a warning on a screen.
/// </summary>
public static class MaterialLotStatuses
{
    public const string Available = "AVAILABLE";

    /// <summary>Under supplier recall or quality hold. Not promisable and not shippable.</summary>
    public const string Quarantined = "QUARANTINED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Available, Quarantined
    };
}

/// <summary>Why a lot was held. Recorded as a code so a recall sweep is a query, not a text search.</summary>
public static class MaterialLotQuarantineReasons
{
    public const string SupplierRecall = "SUPPLIER_RECALL";
    public const string QualityHold = "QUALITY_HOLD";
    public const string CertificateDefect = "CERTIFICATE_DEFECT";
    public const string DamageSuspected = "DAMAGE_SUSPECTED";
    public const string Other = "OTHER";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        SupplierRecall, QualityHold, CertificateDefect, DamageSuspected, Other
    };
}

/// <summary>
/// FR-MTR-02. The compliance evidence a KSA trading business has to be able to produce on demand.
/// A closed set rather than free text: "which lots have an expired Certificate of Conformity" has
/// to be answerable as a query, and free text makes that a guess.
/// </summary>
public static class MaterialCertificateTypes
{
    public const string Manufacturer = "MANUFACTURER";
    public const string CertificateOfOrigin = "CERTIFICATE_OF_ORIGIN";
    public const string CertificateOfConformity = "CERTIFICATE_OF_CONFORMITY";
    public const string Saso = "SASO";
    public const string Saber = "SABER";
    public const string MillTestReport = "MILL_TEST_REPORT";
    public const string Other = "OTHER";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Manufacturer, CertificateOfOrigin, CertificateOfConformity, Saso, Saber, MillTestReport, Other
    };
}

/// <summary>
/// The live expiry verdict for one certificate or for a lot as a whole.
///
/// <para><b>Never stored.</b> This is derived at every read from <c>ExpiresOn</c> against the
/// current date. A stored status column would be correct on the day it was written and wrong every
/// day after — and the failure mode is silent, because a row saying VALID looks exactly like a row
/// that has been checked.</para>
/// </summary>
public static class CertificateExpiryStates
{
    /// <summary>The document carries no expiry. A certificate of origin normally does not.</summary>
    public const string NotApplicable = "NOT_APPLICABLE";

    public const string Valid = "VALID";

    /// <summary>Expires within <see cref="MaterialLotCertificate.ExpiringSoonWindowDays"/>.</summary>
    public const string ExpiringSoon = "EXPIRING_SOON";

    public const string Expired = "EXPIRED";
}

/// <summary>
/// The compliance verdict recorded on a fulfilment at the moment the lot was declared against it.
///
/// <para>Snapshotted rather than recomputed, because "was the conformity certificate valid on the
/// day we shipped" is the question an auditor asks, and recomputing it against today's date answers
/// a different question that happens to look the same.</para>
/// </summary>
public static class MaterialLotComplianceStates
{
    /// <summary>Every certificate on the lot that carries an expiry was in date.</summary>
    public const string Compliant = "COMPLIANT";

    /// <summary>At least one certificate type on the lot had lapsed. Requires a recorded override.</summary>
    public const string CertificateExpired = "CERTIFICATE_EXPIRED";

    /// <summary>The lot carries no certificates at all. Visible, but not a block — see the service.</summary>
    public const string NoCertificate = "NO_CERTIFICATE";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Compliant, CertificateExpired, NoCertificate
    };
}

/// <summary>
/// FR-MTR-01. A quantity of material that entered the building as one identifiable receipt line,
/// carrying where it came from and what it is.
///
/// <para><b>Created by receipt, never by hand.</b> There is no create endpoint. The only writer is
/// <c>IMaterialLotRecorder</c>, called from inside
/// <c>ProcurementApplicationService.PostGoodsReceiptAsync</c>'s transaction, so a goods receipt
/// cannot commit stock without committing the lot that explains it. A hand-created lot would be a
/// lot with no supplier purchase order behind it — that is, an untraceable one, which is the
/// opposite of the point.</para>
///
/// <para><b>Where-from is carried, not derived.</b> <see cref="SupplierPurchaseOrderId"/>,
/// <see cref="SupplierId"/> and <see cref="CommercialCaseId"/> are stamped at receipt rather than
/// re-joined at read time. A supplier recall arrives naming a supplier and a date range, and the
/// answer has to be one indexed predicate over declared columns — not a five-table walk whose
/// result silently changes when someone amends the purchase order afterwards.</para>
/// </summary>
public sealed class MaterialLot
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>
    /// The supplier's batch/lot number, the unit's serial number, or a receipt-derived identifier
    /// for untracked material. Never null — an unnamed lot cannot be quoted back at us by a
    /// supplier issuing a recall.
    /// </summary>
    public string LotNumber { get; set; } = null!;

    /// <summary>
    /// Stamped from the item master at receipt and immutable afterwards. A product switched from
    /// batch to serial tracking next year must not rewrite what last year's receipts were.
    /// </summary>
    public string TrackingMode { get; set; } = MaterialLotTrackingModes.Untracked;

    public long ProductId { get; set; }
    public long InventoryId { get; set; }
    public long WarehouseId { get; set; }

    // ---- Where-from (FR-MTR-03) ------------------------------------------------------------

    public long SupplierPurchaseOrderId { get; set; }
    public long SupplierPurchaseOrderLineId { get; set; }
    public long GoodsReceiptId { get; set; }
    public long SupplierId { get; set; }

    /// <summary>
    /// The commercial case this material was bought for, copied from the purchase order.
    /// Null on a STOCK replenishment lot, which legitimately has no customer case behind it.
    /// </summary>
    public long? CommercialCaseId { get; set; }

    public string? NexoraSerial { get; set; }

    // ---- Customs and compliance identity (FR-MTR-04) ---------------------------------------

    /// <summary>
    /// The origin of the material that actually arrived.
    ///
    /// <para>Distinct from <see cref="OrderedCountryOfOrigin"/> on purpose. The purchase order line
    /// records what was ordered; a supplier can and does ship from a different plant, and the
    /// certificate of origin in the clearance file describes the goods on the pallet, not the
    /// goods on the order. Storing one field for both would make the two indistinguishable and
    /// would silently overwrite the ordered position when a receipt corrected it.</para>
    /// </summary>
    public string? CountryOfOrigin { get; set; }

    /// <summary>
    /// What the purchase order line said when this lot was received. Snapshotted rather than
    /// joined, so a later trade-terms amendment cannot retro-fit agreement between the order and a
    /// receipt that in fact disagreed with it.
    /// </summary>
    public string? OrderedCountryOfOrigin { get; set; }

    /// <summary>FR-MTR-04. Who made it — the half of "origin and manufacturer" that had no home.</summary>
    public string? ManufacturerName { get; set; }

    public string? ManufacturerPartNumber { get; set; }

    /// <summary>The supplier's own delivery-note or batch reference, when it differs from the lot number.</summary>
    public string? SupplierBatchReference { get; set; }

    public DateOnly? ManufactureDate { get; set; }

    /// <summary>
    /// Shelf life of the material itself, distinct from any certificate expiry. Sealant and
    /// batteries expire; the paperwork that describes them expires separately and for other reasons.
    /// </summary>
    public DateOnly? ExpiryDate { get; set; }

    // ---- Quantities ------------------------------------------------------------------------

    public decimal QuantityReceived { get; set; }

    /// <summary>
    /// How much of this lot has been declared against a customer fulfilment. Maintained by
    /// <c>DeclareConsumptionAsync</c>, never by hand, and never above
    /// <see cref="QuantityReceived"/> (enforced by check constraint as well as by the service).
    /// </summary>
    public decimal QuantityConsumed { get; set; }

    public decimal QuantityRemaining => QuantityReceived - QuantityConsumed;

    // ---- Quarantine (FR-MTR-05) ------------------------------------------------------------

    public string Status { get; set; } = MaterialLotStatuses.Available;

    public string? QuarantineReasonCode { get; set; }
    public string? QuarantineReason { get; set; }
    public DateTime? QuarantinedOn { get; set; }
    public string? QuarantinedBy { get; set; }

    /// <summary>
    /// The release is as auditable as the hold. FR-MTR-05 says "until authorized release"; an
    /// unattributed release with no reason is not an authorization, it is a checkbox.
    /// </summary>
    public DateTime? ReleasedOn { get; set; }

    public string? ReleasedBy { get; set; }
    public string? ReleaseReason { get; set; }

    public DateTime ReceivedOn { get; set; }
    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }

    public ICollection<MaterialLotCertificate> Certificates { get; set; } = new List<MaterialLotCertificate>();

    /// <summary>True when what arrived did not come from where it was ordered from.</summary>
    public bool OriginDiffersFromOrder =>
        !string.IsNullOrWhiteSpace(CountryOfOrigin)
        && !string.IsNullOrWhiteSpace(OrderedCountryOfOrigin)
        && !string.Equals(CountryOfOrigin.Trim(), OrderedCountryOfOrigin.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// FR-MTR-02. One compliance document held against one lot.
///
/// <para><b>The bytes are not stored here.</b> <see cref="AttachmentId"/> points at the row written
/// by the same content-addressed, malware-inspected, digest-verified pipeline every other upload
/// door in this product uses. A second upload path would be a second place for an unscanned file to
/// enter, and a second set of integrity guarantees to keep in step with the first.</para>
///
/// <para><b>Renewal needs no workflow.</b> Several certificates of the same type may sit on one
/// lot; the governing expiry for that type is the latest one. Uploading a renewed certificate of
/// conformity therefore makes the lot compliant again by existing, which is what the warehouse
/// actually does, rather than requiring somebody to also remember to retire the old row.</para>
/// </summary>
public sealed class MaterialLotCertificate
{
    /// <summary>How far ahead <see cref="CertificateExpiryStates.ExpiringSoon"/> looks.</summary>
    public const int ExpiringSoonWindowDays = 30;

    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long MaterialLotId { get; set; }

    public string CertificateType { get; set; } = null!;

    public string? CertificateNumber { get; set; }
    public string? IssuedBy { get; set; }
    public DateOnly? IssuedOn { get; set; }

    /// <summary>
    /// Null means this document does not expire, which is a fact about the document — a certificate
    /// of origin describes where goods were made and that never stops being true. It must not be
    /// read as "expiry unknown", which is why the state model distinguishes
    /// <see cref="CertificateExpiryStates.NotApplicable"/> from the dated states.
    /// </summary>
    public DateOnly? ExpiresOn { get; set; }

    public long AttachmentId { get; set; }

    /// <summary>
    /// The digest of the bytes this row means. Copied from the attachment at upload so the
    /// certificate record names a specific document rather than "whatever is at that id now".
    /// </summary>
    public string ContentSha256 { get; set; } = null!;

    public string FileName { get; set; } = null!;
    public string? Notes { get; set; }

    public DateTime UploadedOn { get; set; }
    public string UploadedBy { get; set; } = null!;

    /// <summary>Live expiry verdict for this document as of <paramref name="today"/>.</summary>
    public string ExpiryState(DateOnly today)
    {
        if (ExpiresOn is not { } expires) return CertificateExpiryStates.NotApplicable;
        if (expires < today) return CertificateExpiryStates.Expired;
        return expires.DayNumber - today.DayNumber <= ExpiringSoonWindowDays
            ? CertificateExpiryStates.ExpiringSoon
            : CertificateExpiryStates.Valid;
    }
}

/// <summary>
/// FR-MTR-01 and FR-MTR-03. The declared statement that this lot fulfilled this sales-order line,
/// and (once despatched) went out on this delivery note.
///
/// <para><b>This row is what where-used trace reads.</b> Not a join from the order to the product to
/// the inventory row to whatever lots happen to sit there — that walk answers "which lots COULD have
/// gone" and presents it as "which lots DID". Where a fulfilment cannot state its lot there is no
/// row here, and the trace reports the missing quantity as a gap rather than filling it in with a
/// plausible guess. A visible gap is recoverable; a confident wrong answer is not.</para>
/// </summary>
public sealed class MaterialLotConsumption
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long MaterialLotId { get; set; }

    public long OrderId { get; set; }
    public long OrderItemId { get; set; }

    /// <summary>
    /// The delivery note this lot left on. Null while the lot is committed to the order but not yet
    /// despatched — a real state, and one the trace reports as "declared, not yet shipped" rather
    /// than as a defect.
    /// </summary>
    public long? ShipmentId { get; set; }

    /// <summary>Copied from the order so the declaration states its own case, exactly as the
    /// despatch note and the supplier purchase order do.</summary>
    public long? CommercialCaseId { get; set; }

    public string? NexoraSerial { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>
    /// The certificate position at the moment of declaration. See
    /// <see cref="MaterialLotComplianceStates"/> for why this is snapshotted.
    /// </summary>
    public string ComplianceStateAtDeclaration { get; set; } = MaterialLotComplianceStates.Compliant;

    /// <summary>
    /// Required when <see cref="ComplianceStateAtDeclaration"/> is
    /// <see cref="MaterialLotComplianceStates.CertificateExpired"/>. Someone accepted a documented
    /// risk; their name and their reason are the record of it.
    /// </summary>
    public string? ComplianceOverrideReason { get; set; }

    public string? ComplianceOverrideBy { get; set; }

    public string IdempotencyKey { get; set; } = null!;
    public DateTime DeclaredOn { get; set; }
    public string DeclaredBy { get; set; } = null!;
    public long Version { get; set; } = 1;
}
