using ERP_RFQ_Automation.Delivery;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Gate 7 / Module 7 — the governed delivery half of a shipment.
///
/// <para>Kept in its own partial rather than added to the scaffolded <c>Shipment.cs</c>, in the
/// same way Gate 5 kept traceability out of the scaffolded context: the scaffold is regenerated,
/// and a hand-written invariant living inside a regenerated file is an invariant with a deletion
/// date.</para>
/// </summary>
public partial class Shipment
{
    /// <summary>
    /// FR-DLM-05. The governed lifecycle, one of <see cref="DeliveryStatuses"/>.
    ///
    /// <para><b>Why a second status column, next to <c>StatusId</c>.</b> <c>StatusId</c> is a
    /// foreign key into <c>SetupMaster</c> with <c>SetupType = 'ShipmentStatus'</c> — a picklist
    /// each tenant authors for itself, with nothing seeding it and nothing constraining the words.
    /// It is a label. This is a state. FR-DLM-02 needs "delivered" to be a value the database
    /// recognises before cumulative delivered quantity can mean anything, and no amount of string
    /// matching on a tenant's own vocabulary gets there — the pattern-matching colour lookup in
    /// <c>ShipmentViewPage</c> is what that approach looks like when it is finished.</para>
    ///
    /// <para><c>StatusId</c> is deliberately left alone rather than migrated away. It is a
    /// non-nullable foreign key on an existing table with an existing writer, and collapsing a
    /// tenant's own labels into six governed codes is a data decision the product owner has not
    /// been asked to make. The two coexist: the tenant's word is what the operator reads, and the
    /// governed code is what every guard, ledger and document depends on. <b>Named gap:</b> nothing
    /// keeps them in step, so a tenant whose picklist says "Delivered" on a shipment this column
    /// calls DISPATCHED will show a contradiction. Reconciling or retiring <c>StatusId</c> is a
    /// migration-owner task and is recorded rather than half-done here.</para>
    /// </summary>
    public string DeliveryStatus { get; set; } = DeliveryStatuses.Scheduled;

    public DateTime? DeliveryStatusChangedOn { get; set; }

    public string? DeliveryStatusChangedBy { get; set; }

    /// <summary>
    /// FR-DLM-01. The delivery address mapped to the tenant's governed region master.
    ///
    /// <para><c>ShippingAddress</c> is 500 characters of free text. A delivery note has to name the
    /// region it delivered into — that is the whole point of the BRD's "mapped to the Saudi
    /// region/city master" — and a substring of a free-text blob is not a mapping, it is a guess.
    /// The city is the field an operator actually knows; the region is its parent state, read from
    /// the master rather than typed, which is why only the city is stored.</para>
    ///
    /// <para>This reuses <c>SetCity</c>/<c>SetState</c>, the existing tenant-populated reference
    /// shell that <c>CommercialRoutingApplicationService</c> already reads to derive a sales
    /// territory. A second geography master would have meant a shipment could deliver into a region
    /// the routing engine had never heard of.</para>
    ///
    /// <para>Nullable, and its absence is shown on the note as an absence. Refusing to despatch
    /// because a tenant has not populated its city master would make an empty reference table stop
    /// the warehouse.</para>
    /// </summary>
    public int? DeliveryCityId { get; set; }

    public virtual SetCity? DeliveryCity { get; set; }

    /// <summary>
    /// The goods are out of the warehouse. Drives the despatched half of FR-DLM-02.
    /// </summary>
    public bool HasDespatched => DeliveryStatuses.Despatched.Contains(DeliveryStatus);

    /// <summary>
    /// The customer has taken receipt and a proof of delivery exists. Drives the accepted half.
    /// </summary>
    public bool IsConfirmed => DeliveryStatuses.Confirmed.Contains(DeliveryStatus);

    /// <summary>
    /// Moves the shipment along the ladder, or refuses.
    ///
    /// <para>The refusal is an exception rather than a bool because every caller that could ignore
    /// a false is a caller that would report success while changing nothing — wiring contract
    /// failure #7. <see cref="DeliveryStatuses.Delivered"/> and
    /// <see cref="DeliveryStatuses.DeliveryException"/> are unreachable through here by design;
    /// they are applied by the confirmation path, from the quantities the customer accepted.</para>
    /// </summary>
    public void TransitionDeliveryStatus(string next, string actor, DateTime occurredOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(next);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (!DeliveryStatuses.CanTransition(DeliveryStatus, next))
            throw new InvalidOperationException(
                $"A shipment cannot move from {DeliveryStatus} to {next}.");

        DeliveryStatus = next;
        DeliveryStatusChangedBy = actor;
        DeliveryStatusChangedOn = occurredOn;
    }

    /// <summary>
    /// Applied only by the confirmation path, which has already decided the outcome from the
    /// accepted quantities. Separate from <see cref="TransitionDeliveryStatus"/> precisely so that
    /// no operator-facing route can reach it.
    /// </summary>
    internal void ApplyConfirmedOutcome(string outcome, string actor, DateTime occurredOn)
    {
        if (!DeliveryStatuses.Confirmed.Contains(outcome))
            throw new InvalidOperationException($"{outcome} is not a delivery confirmation outcome.");
        if (!DeliveryStatuses.Confirmable.Contains(DeliveryStatus))
            throw new InvalidOperationException(
                $"A {DeliveryStatus} shipment cannot be confirmed received.");

        DeliveryStatus = outcome;
        DeliveryStatusChangedBy = actor;
        DeliveryStatusChangedOn = occurredOn;
    }
}
