namespace ERP_RFQ_Automation.SupplierEvaluation;

/// <summary>
/// The per-tenant weight set the supplier-quote comparison score is computed from (FR-QTM-03).
/// Four numbers totalling 100 — price, lead time, warranty, payment terms — one row per business
/// unit, created on demand with the defaults below.
///
/// <para><b>Why a table and not constants.</b> A weighted recommendation already existed with the
/// weights hardcoded in <c>Agent/Sourcing/SupplierScoring.cs</c>, so no customer could say what
/// mattered to their business. A trader whose customers award on delivery date and one who awards on
/// price are not running the same comparison, and the requirement is explicit that the weights are
/// configurable. Every field here is customer-settable through
/// <see cref="SupplierComparisonWeightsService"/> and the Commercial Policy settings screen; a
/// change re-ranks every supplier comparison computed after it, so a write requires a reason and
/// stamps <see cref="ModifiedBy"/>/<see cref="ModifiedOn"/>/<see cref="Version"/>.</para>
///
/// <para><b>"Not configured" is the ABSENCE of the row, not a null field.</b> Unlike the tolerances
/// on <c>CommercialMatchingPolicy</c>, a single weight has no meaning on its own — the set must
/// total 100 or the score cannot be presented as "82 out of 100" — so the four are stored non-null
/// and written together. A tenant that has never saved reads <see cref="DefaultFor"/> and the view
/// says <c>IsDefault</c>, exactly as the tolerances do.</para>
///
/// <para>Exactly four criteria. Coverage is NOT a weight: it stays the existing full-cover-first
/// tiebreak. Supplier tier is NOT a weight either — tier and score are adjacent capabilities, not
/// composable ones.</para>
/// </summary>
public sealed class SupplierComparisonWeights
{
    /// <summary>The weights must total exactly this. See the CHECK constraint on the table.</summary>
    public const int RequiredTotal = 100;

    /// <summary>
    /// Price dominates by default because landed cost is the one criterion every quote carries as a
    /// single authoritative number, and it is what today's hardcoded recommendation already sorted
    /// on after coverage. A tenant that awards on delivery moves the weight.
    /// </summary>
    public const int DefaultPriceWeight = 80;

    public const int DefaultLeadTimeWeight = 20;

    /// <summary>
    /// Zero by default, and that is a decision rather than an omission. Warranty is free text on the
    /// supplier quote today, and a missing weighted criterion is never scored as zero — so a
    /// non-zero default would make almost every offer unscorable on day one. It is a real weight a
    /// customer may raise once warranty is captured numerically; that capture is a flagged
    /// follow-up, not part of this gate.
    /// </summary>
    public const int DefaultWarrantyWeight = 0;

    /// <summary>
    /// Scored from the supplier's <c>CreditDays</c> — more credit is better — and zero by default for
    /// exactly the same reason warranty is, which the Integration Owner's contract originally missed.
    ///
    /// <c>CreditDays</c> ships in this gate, so it is null on every supplier that already exists. A
    /// non-zero default would meet <see cref="WeightedSupplierScoring"/>'s rule that a weighted
    /// criterion with no value is never scored as zero, and the result would be "Cannot score —
    /// payment terms missing" on EVERY row of EVERY comparison until somebody hand-fills credit days
    /// across the whole supplier master. The feature would arrive broken and look like it.
    ///
    /// So the two criteria backed by data that does not exist yet start at zero, and the two backed
    /// by data every quote already carries — landed cost and lead time — carry the whole score. The
    /// customer raises this the day they start recording credit terms, and the Setup screen warns
    /// them when they weight a criterion the data does not support yet.
    /// </summary>
    public const int DefaultPaymentTermsWeight = 0;

    /// <summary>
    /// The weight set every read falls back to when a tenant has no row yet. Reading it is what
    /// makes "create on demand" safe to skip on pure read paths: absence of a row means defaults,
    /// and the defaults are stated once, here, in the entity itself rather than at each call site.
    /// </summary>
    public static SupplierComparisonWeights DefaultFor(long businessUnitId) =>
        new() { BusinessUnitId = businessUnitId };

    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    /// <summary>Share of the score earned by the lowest landed cost in the candidate set. 0–100.</summary>
    public int PriceWeight { get; set; } = DefaultPriceWeight;

    /// <summary>Share of the score earned by the shortest lead time in the candidate set. 0–100.</summary>
    public int LeadTimeWeight { get; set; } = DefaultLeadTimeWeight;

    /// <summary>Share of the score earned by the longest warranty in the candidate set. 0–100.</summary>
    public int WarrantyWeight { get; set; } = DefaultWarrantyWeight;

    /// <summary>Share of the score earned by the most credit days in the candidate set. 0–100.</summary>
    public int PaymentTermsWeight { get; set; } = DefaultPaymentTermsWeight;

    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }
}
