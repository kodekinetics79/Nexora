namespace ERP_RFQ_Automation.OrderToCash;

/// <summary>
/// The per-tenant commercial policy row: the tolerances that decide whether a customer PO differs
/// from what we quoted (FR-COM-04), and the one switch that decides whether supplier input tax is
/// a cost (<see cref="SupplierInputTaxRecoverable"/>).
///
/// <para>The PO comparison used to be exact decimal equality — not a configurable tolerance, not
/// even a hardcoded one — so ordinary rounding noise read as a commercial discrepancy while the
/// requirement asks for a tolerance "such as plus or minus 2%". One row per business unit,
/// created on demand with the defaults below.</para>
///
/// <para>The BRD leaves the SCOPE of the tolerance open (decision E10: tenant, customer, or price
/// list?). This is the tenant-level answer, which is the level the requirement illustrates. It is
/// deliberately a table rather than a constant so a narrower scope can be added later without
/// moving where the value lives — and that is exactly why the input-tax switch lives here too
/// rather than becoming a second, silent constant somewhere in the pricing code.</para>
/// </summary>
public sealed class CommercialMatchingPolicy
{
    /// <summary>
    /// The policy every read falls back to when a tenant has no row yet. Reading it is what makes
    /// "create on demand" safe to skip on pure read paths: absence of a row means defaults, and
    /// the defaults are stated once, here, in the entity itself rather than at each call site.
    /// </summary>
    public static CommercialMatchingPolicy DefaultFor(long businessUnitId) =>
        new() { BusinessUnitId = businessUnitId };

    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    /// <summary>
    /// Whether the tax a Supplier charges us on a purchase is recoverable from the tax authority,
    /// and therefore NOT a cost of the goods.
    ///
    /// <para>Default TRUE, because the platform's home jurisdiction is Saudi Arabia and a
    /// VAT-registered business reclaims its input VAT: the 15% the supplier adds to an invoice is
    /// a receivable from ZATCA, not money the goods consumed. Carrying it in landed cost overstated
    /// cost, and because a customer price is derived as landed / (1 - margin), the overstatement was
    /// then MARKED UP by the margin before output VAT was added again on the customer quote — the
    /// same tax counted twice, once with profit on top.</para>
    ///
    /// <para>Set FALSE for a business unit that cannot reclaim input tax — one that is not
    /// VAT-registered, or that trades exclusively in exempt supplies — for which the supplier's tax
    /// genuinely is a cost of acquisition and belongs in landed cost.</para>
    ///
    /// <para>This is deliberately ONE boolean per business unit and not a tax-code engine. Decision
    /// R8 fixes the cost model at "landed price, margin, tax" and rules out per-line jurisdiction or
    /// recoverability inference: "the more complex the harder for them". Freight, duty and other
    /// captured charges are unaffected by this switch — they are never recoverable and always land
    /// in cost.</para>
    /// </summary>
    public bool SupplierInputTaxRecoverable { get; set; } = true;

    /// <summary>Percentage difference tolerated between the quoted and ordered unit price.</summary>
    public decimal PriceTolerancePercent { get; set; } = 2.0m;

    /// <summary>
    /// Absolute difference tolerated regardless of percentage.
    ///
    /// <para>A percentage alone misbehaves at small values: a line quoted at 0.10 and ordered at
    /// 0.11 is a 10% swing and almost certainly rounding, while the same percentage on a 50,000
    /// line is a real commercial difference. The two together mean a discrepancy is reported when
    /// it is both proportionally and absolutely material.</para>
    /// </summary>
    public decimal PriceToleranceMinimumAmount { get; set; } = 0m;

    /// <summary>
    /// Percentage difference tolerated between the quoted and ordered quantity. Defaults to zero:
    /// a quantity is a count the buyer stated, so any difference is a real award decision — a
    /// partial award — and not noise to be absorbed.
    /// </summary>
    public decimal QuantityTolerancePercent { get; set; } = 0m;

    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }
}
