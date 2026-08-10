using ERP_RFQ_Automation.Sla;

namespace ERP_RFQ_Automation.InboundLogistics;

/// <summary>Which fact the Material Available Date was calculated from.</summary>
public static class MaterialAvailableBasisKinds
{
    /// <summary>The carrier's estimate. Everything downstream is still forecast.</summary>
    public const string Eta = "ETA";

    /// <summary>The shipment is physically at the entry point. Clearance and putaway remain.</summary>
    public const string ArrivedSaudiPort = "ARRIVED_SAUDI_PORT";

    /// <summary>Customs is done, so no clearance time is added. Only putaway remains.</summary>
    public const string CustomsCleared = "CUSTOMS_CLEARANCE";

    /// <summary>Goods are at the warehouse. Only putaway remains.</summary>
    public const string ReceivedAtWarehouse = "RECEIVED_AT_WAREHOUSE";
}

/// <summary>
/// The outcome of one derivation, carrying its own inputs.
///
/// <para><see cref="Date"/> is null whenever the calculation could not be made, and
/// <see cref="UnavailableReason"/> then carries the sentence a user reads. There is deliberately no
/// third state: a date is either derived from stated inputs, or it does not exist.</para>
/// </summary>
public sealed record MaterialAvailabilityResult(
    DateOnly? Date,
    string? BasisKind,
    DateOnly? BasisDate,
    int? CustomsClearanceDays,
    int? PutawayDays,
    string? UnavailableReason);

/// <summary>
/// FR-MAS-03. Material Available Date = shipment ETA + customs-clearance lead time + putaway lead
/// time.
///
/// <para><b>The basis moves as facts replace forecasts.</b> While the shipment is still travelling
/// the only input available is the carrier's ETA, so the full clearance and putaway allowance is
/// added to it. Once the shipment is recorded as arrived at a Saudi entry point, the ACTUAL arrival
/// date replaces the estimate — an ETA that has already been overtaken by events is not evidence.
/// Once customs is cleared the clearance allowance is no longer a forecast of anything, so it drops
/// to zero and only putaway remains; the same applies at warehouse receipt. A calculation that kept
/// adding a five-day clearance allowance to a shipment already cleared through Jeddah would push the
/// availability date a week beyond the truth and lose a customer promise that could have been made.</para>
///
/// <para><b>Working days, not calendar days.</b> Customs offices and warehouses are shut on the
/// Saudi weekend, so <see cref="BusinessCalendar"/> — Friday and Saturday — does the arithmetic.
/// This is the same calendar the SLA sweeps count with; using calendar days here and working days
/// there would make the alert and the date it alerts on disagree. Public holidays are not modelled,
/// which is <see cref="BusinessCalendar"/>'s stated and accepted limit.</para>
///
/// <para><b>No input is invented.</b> A missing ETA, an unconfigured lead time or a cancelled
/// shipment each produce a null date with a stated reason rather than a plausible-looking guess. The
/// standing lesson from the Gate 4 SLA backfill is that a zero silently substituted for "not
/// configured" fails confidently and invisibly; the equivalent failure here is a Material Available
/// Date equal to the ETA, promised to a customer, on every shipment in the tenant.</para>
/// </summary>
public static class MaterialAvailabilityCalculator
{
    /// <summary>
    /// Derives the Material Available Date for one shipment under one policy.
    /// </summary>
    public static MaterialAvailabilityResult Derive(SupplierShipment shipment, InboundLogisticsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        ArgumentNullException.ThrowIfNull(policy);

        if (string.Equals(shipment.Milestone, InboundShipmentMilestones.Cancelled, StringComparison.Ordinal))
            return new MaterialAvailabilityResult(null, null, null, null, null,
                "This shipment was cancelled, so no material will become available from it.");

        if (!policy.IsConfigured)
            return new MaterialAvailabilityResult(null, null, null,
                policy.CustomsClearanceLeadDays, policy.PutawayLeadDays,
                "Customs-clearance and putaway lead times have not been set for this business unit, "
                + "so a material available date cannot be derived. Set them in the inbound logistics policy.");

        var customs = policy.CustomsClearanceLeadDays!.Value;
        var putaway = policy.PutawayLeadDays!.Value;

        // Facts beat forecasts, latest fact first.
        if (shipment.ReceivedAtWarehouseOn is { } received)
            return Compute(MaterialAvailableBasisKinds.ReceivedAtWarehouse, received, 0, putaway);

        if (shipment.CustomsClearanceOn is { } cleared)
            return Compute(MaterialAvailableBasisKinds.CustomsCleared, cleared, 0, putaway);

        if (shipment.ArrivedSaudiPortOn is { } arrived)
            return Compute(MaterialAvailableBasisKinds.ArrivedSaudiPort, arrived, customs, putaway);

        if (shipment.EtaDate is { } eta)
            return Compute(MaterialAvailableBasisKinds.Eta, eta, customs, putaway);

        return new MaterialAvailabilityResult(null, null, null, customs, putaway,
            "This shipment has no ETA and has not reached a Saudi entry point, so there is nothing "
            + "to calculate a material available date from. Record the carrier's ETA.");
    }

    private static MaterialAvailabilityResult Compute(
        string basisKind, DateOnly basisDate, int customsDays, int putawayDays)
    {
        var date = BusinessCalendar.AddBusinessDays(basisDate, customsDays);
        date = BusinessCalendar.AddBusinessDays(date, putawayDays);
        return new MaterialAvailabilityResult(date, basisKind, basisDate, customsDays, putawayDays, null);
    }

    /// <summary>Writes a derivation onto the shipment, inputs and all.</summary>
    public static void Apply(SupplierShipment shipment, MaterialAvailabilityResult result, DateTime now)
    {
        shipment.MaterialAvailableDate = result.Date;
        shipment.MaterialAvailableBasisKind = result.BasisKind;
        shipment.MaterialAvailableBasisDate = result.BasisDate;
        shipment.AppliedCustomsClearanceDays = result.CustomsClearanceDays;
        shipment.AppliedPutawayDays = result.PutawayDays;
        shipment.MaterialAvailableUnavailableReason = result.UnavailableReason;
        shipment.MaterialAvailableComputedOn = now;
    }
}
