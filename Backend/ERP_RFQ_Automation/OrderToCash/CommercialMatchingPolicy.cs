namespace ERP_RFQ_Automation.OrderToCash;

/// <summary>
/// FR-COM-04. The tolerances that decide whether a customer PO differs from what we quoted.
///
/// <para>The comparison used to be exact decimal equality — not a configurable tolerance, not
/// even a hardcoded one — so ordinary rounding noise read as a commercial discrepancy while the
/// requirement asks for a tolerance "such as plus or minus 2%". One row per business unit,
/// created on demand with the defaults below.</para>
///
/// <para>The BRD leaves the SCOPE of the tolerance open (decision E10: tenant, customer, or price
/// list?). This is the tenant-level answer, which is the level the requirement illustrates. It is
/// deliberately a table rather than a constant so a narrower scope can be added later without
/// moving where the value lives.</para>
/// </summary>
public sealed class CommercialMatchingPolicy
{
    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

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
