namespace ERP_RFQ_Automation.OrderToCash;

/// <summary>
/// THE comparison behind FR-COM-04's customer-PO discrepancy report: is the number the buyer wrote
/// close enough to the number we quoted to be rounding rather than a different commercial deal?
///
/// Why this file exists
/// --------------------
/// <para><see cref="CommercialMatchingPolicy.PriceTolerancePercent"/>,
/// <see cref="CommercialMatchingPolicy.PriceToleranceMinimumAmount"/> and
/// <see cref="CommercialMatchingPolicy.QuantityTolerancePercent"/> were settable through the
/// Commercial Policy screen, persisted, versioned, audited — and read by nothing. The only
/// comparison in the award path was exact decimal equality, so a manager could set 2%, be told it
/// saved, and every sub-halalah rounding difference still raised <c>PRICE_DISCREPANCY</c>. That is
/// the wiring contract's failure #5 and failure #7 at once: a setting with no reader, presented to
/// the user as a working control. The damage is not the noise, it is that a discrepancy report
/// nobody believes stops standing between a mis-keyed customer PO and a wrong invoice.</para>
///
/// Direction
/// ---------
/// <para><b>The tolerance is symmetric on magnitude.</b> A customer PO priced above our quote and
/// one priced below are genuinely different commercial events, and the temptation is to tolerate
/// only the direction that favours the seller. That is rejected here, for three reasons:</para>
/// <list type="number">
/// <item>The stated purpose of the number — on the entity, and in the sentence the settings screen
/// shows the manager ("percentage difference in unit price treated as rounding") — is rounding
/// noise, and rounding has no direction. A one-sided rule leaves half the noise in place, so the
/// report still cries wolf and the control is only half wired.</item>
/// <item>Absorbing a difference does not move any money. The award prices from the QUOTE
/// (<c>CustomerAwardLineAllocation.UnitPriceSnapshot</c>), never from the buyer's PO, so a tolerated
/// under-priced PO costs no margin — it costs a conversation, and a 0.004 SAR conversation is not
/// one worth having.</item>
/// <item>A one-sided rule would contradict its own label. The screen promises a difference
/// tolerance; a manager who set 2% and found it applied downward only would be looking at exactly
/// the kind of control that reports one thing and does another.</item>
/// </list>
/// <para>A tenant that wants "never absorb a customer under-price" is asking for a second, named
/// policy field, not a silent reinterpretation of this one. That field does not exist and is
/// recorded as owing rather than invented here.</para>
///
/// <para>Widening is bounded elsewhere: <c>CommercialMatchingPolicyService.MaximumTolerancePercent</c>
/// caps both percentages at 25, because above that a tolerance stops absorbing rounding and becomes
/// a blanket instruction to accept whatever the customer ordered at.</para>
/// </summary>
public static class CommercialMatchingTolerance
{
    /// <summary>
    /// Whether <paramref name="actual"/> is within tolerance of <paramref name="reference"/>.
    ///
    /// <para>The allowance is <c>max(percent × |reference|, minimumAmount)</c> — both together,
    /// because a percentage alone misbehaves at small values (0.10 quoted against 0.11 ordered is a
    /// 10% swing and almost certainly rounding) while an absolute floor alone would absorb real
    /// money on a large line. A difference is reported only when it clears BOTH.</para>
    ///
    /// <para>Zero percent and zero minimum reproduce exact equality exactly, which is what the
    /// quantity tolerance defaults to and is why turning the control off is a supported state rather
    /// than a special case.</para>
    /// </summary>
    /// <param name="actual">The number on the buyer's document.</param>
    /// <param name="reference">The number the tolerance is a percentage OF — what we quoted.</param>
    /// <param name="percent">Percentage of <paramref name="reference"/> to tolerate, 0–100.</param>
    /// <param name="minimumAmount">Absolute allowance tolerated regardless of percentage.</param>
    public static bool Within(decimal actual, decimal reference, decimal percent, decimal minimumAmount)
    {
        var difference = Math.Abs(actual - reference);
        if (difference == 0m) return true;
        // Negative policy values cannot reach here through the settings service, which bounds both
        // at >= 0. Clamping anyway so a row edited by direct SQL cannot make the allowance negative
        // and turn a tolerance into a trigger.
        var allowance = Math.Max(
            Math.Max(0m, percent) / 100m * Math.Abs(reference),
            Math.Max(0m, minimumAmount));
        return difference <= allowance;
    }

    /// <summary>
    /// Whether the unit price on the buyer's PO line matches what we quoted, within the tenant's
    /// price tolerance. The percentage is taken against the QUOTED price: that is the figure the
    /// manager is thinking of when they type 2%, and it is the stable side of the comparison.
    /// </summary>
    public static bool PriceMatches(decimal orderedUnitPrice, decimal quotedUnitPrice,
        CommercialMatchingPolicy policy) =>
        Within(orderedUnitPrice, quotedUnitPrice, policy.PriceTolerancePercent,
            policy.PriceToleranceMinimumAmount);

    /// <summary>
    /// Whether the quantity we awarded matches the quantity the buyer ordered, within the tenant's
    /// quantity tolerance. The percentage is taken against the ORDERED quantity, because the buyer's
    /// document is the thing the award is being checked against.
    ///
    /// <para>There is no minimum-amount companion: a quantity has no sub-unit rounding the way a
    /// price does, and the default of 0% means any difference is reported — a quantity is a count
    /// the buyer stated, so a difference is a real award decision rather than noise. The percentage
    /// exists for fractional units of measure (kg, metres) where a conversion can round either
    /// way.</para>
    /// </summary>
    public static bool QuantityMatches(decimal awardedQuantity, decimal orderedQuantity,
        CommercialMatchingPolicy policy) =>
        Within(awardedQuantity, orderedQuantity, policy.QuantityTolerancePercent, 0m);
}
