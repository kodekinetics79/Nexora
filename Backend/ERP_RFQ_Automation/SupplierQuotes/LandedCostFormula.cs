namespace ERP_RFQ_Automation.SupplierQuotes;

/// <summary>
/// THE definition of a supplier line's landed unit cost. One formula, one place.
///
/// Why this file exists
/// --------------------
/// Two implementations of "landed unit cost" used to coexist and disagree:
///
/// * <c>SupplierQuoteCommercialService.ProjectAsync</c> allocated freight and tax by
///   QUANTITY (<c>FreightAmount * quantity / totalQuantity</c>) and persisted the result to
///   <c>SupplierQuotedItem.LandedUnitCost</c> — the number every award, purchase order and
///   customer price is then built on.
/// * <c>SupplierNegotiationService.CurrentLandedUnitCost</c> recomputed the same quantity by
///   VALUE (<c>(freight + tax) * (lineValue / totalMerchandiseValue) / quantity</c>).
///
/// The negotiation service then compared its own recomputation against the persisted figure
/// (<c>PostSelectionIncreaseEvidence</c>) and raised POST_SELECTION_PRICE_INCREASE — severity
/// CRITICAL, <c>Blocking = true</c> — whenever the current round exceeded the selected snapshot
/// by 2%. On any revision whose lines differ in unit price the two formulas differ by far more
/// than 2%, so the flag fired on quotes that had not changed at all: a blocking control driven
/// by an arithmetic disagreement rather than by supplier behaviour.
///
/// Which definition survived, and why
/// ----------------------------------
/// VALUE-weighted allocation. Freight and tax are charges on the commercial value shipped, not
/// on the count of lines, and quantity-weighting is simply wrong the moment two lines are quoted
/// in different units of measure: 30 EA and 1 LOT are not 31 comparable things, so allocating
/// 30/31 of the freight to the EA line has no commercial meaning. This is the semantic the
/// negotiation service already implemented and that
/// <c>V2Gate04SupplierNegotiationServiceTests.Shared_charges_are_allocated_by_commercial_value_across_mixed_units</c>
/// pins down. The canonical projection was therefore moved onto this definition rather than the
/// other way round.
///
/// Scale is 4dp throughout, matching the persisted <c>SupplierQuotedItem</c> columns and the
/// values both call sites already produced.
///
/// What landed cost contains, and what it does not
/// -----------------------------------------------
/// Freight, duty and other captured charges are IN: they are non-recoverable costs of getting the
/// goods to the warehouse, and nobody refunds them.
///
/// Recoverable supplier input tax is OUT. In Saudi Arabia a VAT-registered business reclaims the
/// input VAT a supplier charges it, so that 15% is a receivable from ZATCA, not money the goods
/// consumed. This formula used to fold it in, with three compounding consequences:
///
///   1. cost was overstated by the tax;
///   2. the customer price is derived as <c>landed / (1 - margin)</c>, so the tax was not merely
///      passed through — it was MARKED UP by the target margin;
///   3. output VAT is then added again on the customer quote (<c>QuoteItem.TaxAmount</c>), so the
///      customer was charged tax on tax, and every reported gross margin was wrong.
///
/// Whether the tax is recoverable is a property of the TENANT, not of the line, so it is one named
/// switch — <c>CommercialMatchingPolicy.SupplierInputTaxRecoverable</c>, default true — and not a
/// tax-code engine (decision R8). A business unit that cannot reclaim input tax flips it to false
/// and the tax lands in cost, which for that tenant is the truth.
///
/// <see cref="UnitCost"/> takes that answer as a REQUIRED argument. It has no default on purpose:
/// a caller that has not thought about recoverability should not compile, because the silent
/// default is precisely the defect this replaces.
/// </summary>
public static class LandedCostFormula
{
    /// <summary>Scale of every figure this formula produces, matching SupplierQuotedItem.</summary>
    public const int Scale = 4;

    /// <summary>The commercial value of one line — the basis shared charges are allocated on.</summary>
    public static decimal LineValue(decimal unitPrice, decimal quantity) => unitPrice * quantity;

    /// <summary>The commercial value of a whole round.</summary>
    public static decimal TotalLineValue(IEnumerable<(decimal UnitPrice, decimal Quantity)> lines)
        => lines.Sum(line => LineValue(line.UnitPrice, line.Quantity));

    /// <summary>
    /// One line's share of a round-level charge (freight, tax), allocated by commercial value.
    ///
    /// Returns zero rather than dividing when the round carries no value to allocate against —
    /// the previous quantity-weighted code divided by a total that could legitimately be zero
    /// and would have thrown <see cref="DivideByZeroException"/> mid-transaction.
    /// </summary>
    public static decimal AllocateByValue(decimal chargeAmount, decimal lineValue, decimal totalLineValue)
        => chargeAmount == 0m || totalLineValue <= 0m || lineValue <= 0m
            ? 0m
            : decimal.Round(chargeAmount * lineValue / totalLineValue, Scale);

    /// <summary>
    /// How much of a supplier tax charge is a COST of the goods, given the tenant's policy.
    ///
    /// <para>Recoverable input tax contributes nothing: it is reclaimed from the tax authority, so
    /// it never touches the cost of the goods and must never reach a margin calculation or a
    /// customer price. Non-recoverable input tax contributes in full, because a tenant that cannot
    /// reclaim it really did pay it to acquire the goods.</para>
    ///
    /// <para>Every landed-cost site in the platform routes its tax term through this method, so the
    /// rule lives in exactly one place even where the surrounding arithmetic differs.</para>
    /// </summary>
    public static decimal CostBearingTax(decimal taxAmount, bool supplierInputTaxRecoverable)
        => supplierInputTaxRecoverable ? 0m : taxAmount;

    /// <summary>
    /// Landed cost of ONE unit: the line's own price plus its allocated share of the round's
    /// shared charges, spread over the quantity. A non-positive quantity has no per-unit answer,
    /// so the bare unit price is returned rather than dividing by zero.
    ///
    /// <para><paramref name="allocatedTax"/> is the tax the supplier charged, as captured. Whether
    /// any of it lands in cost is decided by <paramref name="supplierInputTaxRecoverable"/> — the
    /// tenant's <c>CommercialMatchingPolicy.SupplierInputTaxRecoverable</c>. Under the KSA default
    /// (recoverable) the tax term drops out entirely and landed cost is the tax-free build-up.</para>
    /// </summary>
    public static decimal UnitCost(decimal unitPrice, decimal quantity, decimal allocatedFreight,
        decimal allocatedTax, bool supplierInputTaxRecoverable)
        => quantity <= 0m
            ? decimal.Round(unitPrice, Scale)
            : decimal.Round((unitPrice * quantity + allocatedFreight +
                CostBearingTax(allocatedTax, supplierInputTaxRecoverable)) / quantity, Scale);
}
