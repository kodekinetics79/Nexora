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
/// How much of the tax is recoverable is a property of the TENANT, not of the line, so it is one
/// named number — <c>CommercialMatchingPolicy.SupplierInputTaxRecoverablePercent</c>, default 100 —
/// and not a tax-code engine (decision R8). A business unit that cannot reclaim input tax sets 0
/// and the tax lands in cost, which for that tenant is the truth; one that recovers pro-rata sets
/// its ratio and only the irrecoverable share is cost (decision R18, IAS 2.11).
///
/// <see cref="UnitCost"/> takes that answer as a REQUIRED argument. It has no default on purpose:
/// a caller that has not thought about recoverability should not compile, because the silent
/// default is precisely the defect this replaces.
///
/// What this formula is NOT
/// -----------------------
/// It is the BUY side only. Output tax on the customer quote is a different leg with a different
/// base and a different rate, and lives in <c>OrderToCash.OutputTaxFormula</c>. The two were once
/// wrong in opposite directions and cancelled each other out; keeping them in separate files with
/// separate policy fields is what stops that from recurring.
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
    /// How much of a supplier tax charge is a COST of the goods, given the tenant's policy:
    /// <c>taxAmount * (1 - recoverablePercent / 100)</c>.
    ///
    /// <para>Fully recoverable input tax (100) contributes nothing: it is reclaimed from the tax
    /// authority, so it never touches the cost of the goods and must never reach a margin
    /// calculation or a customer price. Fully irrecoverable input tax (0) contributes in full,
    /// because a tenant that cannot reclaim it really did pay it to acquire the goods.</para>
    ///
    /// <para>Everything between is the case a boolean could not express (decision R18): a
    /// VAT-registered trader making partly exempt supplies recovers pro-rata, and the irrecoverable
    /// share is a cost of the asset under IAS 2.11. At 70% recovery, 30% of the supplier's tax is
    /// cost — neither the whole of it nor none of it.</para>
    ///
    /// <para>Every landed-cost site in the platform routes its tax term through this method, so the
    /// rule lives in exactly one place even where the surrounding arithmetic differs. The
    /// percentage is clamped to 0–100 as defence in depth behind the database check constraint: a
    /// value outside that range would otherwise turn a tax charge into a negative cost and quietly
    /// reduce a customer price.</para>
    /// </summary>
    public static decimal CostBearingTax(decimal taxAmount, decimal supplierInputTaxRecoverablePercent)
        => decimal.Round(taxAmount *
            (1m - Math.Clamp(supplierInputTaxRecoverablePercent, 0m, 100m) / 100m), Scale);

    /// <summary>
    /// The fraction of a quoted line's charges that an award for <paramref name="awardedQuantity"/>
    /// bears, given that the line was quoted — and its charges computed — for
    /// <paramref name="quotedQuantity"/>.
    ///
    /// <para>This exists because a partial award used to inherit the WHOLE line's freight, duty and
    /// other charges and then divide them by the awarded quantity alone. Awarding 20 units of a
    /// 100-unit line carrying 1,000 of allocated freight produced a landed unit cost of 150 against
    /// a true 110 — 36% overstated — and the residual award of the other 80 then produced 100,
    /// 9% understated, because the second award was told to carry no charges at all. Both figures
    /// reach <c>SourcingAward.LandedUnitCost</c> and <c>TotalValue</c>, then the supplier purchase
    /// order line and the committed-spend total, and then the customer price by
    /// <c>landed / (1 - margin)</c>.</para>
    ///
    /// <para>Clamped at 1: the award path already refuses an award larger than the quoted quantity,
    /// and a share above 1 would invent charges the supplier never billed.</para>
    /// </summary>
    public static decimal AwardShare(decimal awardedQuantity, decimal quotedQuantity)
        => quotedQuantity <= 0m || awardedQuantity <= 0m
            ? 0m
            : Math.Min(1m, awardedQuantity / quotedQuantity);

    /// <summary>
    /// The part of a quoted line's charge that one award carries, pro-rated by
    /// <see cref="AwardShare"/>. Awards that between them take the whole quoted quantity carry the
    /// whole charge between them, to the rounding scale.
    /// </summary>
    public static decimal ProRateToAward(decimal chargeAmount, decimal awardedQuantity,
        decimal quotedQuantity)
        => chargeAmount == 0m
            ? 0m
            : decimal.Round(chargeAmount * AwardShare(awardedQuantity, quotedQuantity), Scale);

    /// <summary>
    /// Landed cost of ONE unit: the line's own price plus its allocated share of the round's
    /// shared charges, spread over the quantity. A non-positive quantity has no per-unit answer,
    /// so the bare unit price is returned rather than dividing by zero.
    ///
    /// <para><paramref name="allocatedTax"/> is the tax the supplier charged, as captured. How much
    /// of it lands in cost is decided by <paramref name="supplierInputTaxRecoverablePercent"/> — the
    /// tenant's <c>CommercialMatchingPolicy.SupplierInputTaxRecoverablePercent</c>. Under the KSA
    /// default (100, fully recoverable) the tax term drops out entirely and landed cost is the
    /// tax-free build-up.</para>
    ///
    /// <para><paramref name="allocatedDuty"/> is import duty, and it is ALWAYS a cost: nobody
    /// refunds it, and IAS 2.11 names import duties among the costs of purchase. It is a trailing
    /// parameter defaulting to zero because a domestic round genuinely carries none — not as a
    /// convenience. A caller that has captured duty must pass it, and the canonical projection
    /// does. <paramref name="allocatedOther"/> carries clearance, handling and inspection on the
    /// same footing. <paramref name="allocatedDiscount"/> is subtracted: a discount the supplier
    /// granted is money the goods did not cost, and dropping it overstates cost and therefore the
    /// customer price.</para>
    /// </summary>
    public static decimal UnitCost(decimal unitPrice, decimal quantity, decimal allocatedFreight,
        decimal allocatedTax, decimal supplierInputTaxRecoverablePercent, decimal allocatedDuty = 0m,
        decimal allocatedOther = 0m, decimal allocatedDiscount = 0m)
        => quantity <= 0m
            ? decimal.Round(unitPrice, Scale)
            : decimal.Round((unitPrice * quantity + allocatedFreight + allocatedDuty + allocatedOther +
                CostBearingTax(allocatedTax, supplierInputTaxRecoverablePercent) - allocatedDiscount)
                / quantity, Scale);
}
