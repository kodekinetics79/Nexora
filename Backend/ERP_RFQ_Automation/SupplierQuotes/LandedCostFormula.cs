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
    /// Landed cost of ONE unit: the line's own price plus its allocated share of the round's
    /// shared charges, spread over the quantity. A non-positive quantity has no per-unit answer,
    /// so the bare unit price is returned rather than dividing by zero.
    /// </summary>
    public static decimal UnitCost(decimal unitPrice, decimal quantity, decimal allocatedFreight,
        decimal allocatedTax)
        => quantity <= 0m
            ? decimal.Round(unitPrice, Scale)
            : decimal.Round((unitPrice * quantity + allocatedFreight + allocatedTax) / quantity, Scale);
}
