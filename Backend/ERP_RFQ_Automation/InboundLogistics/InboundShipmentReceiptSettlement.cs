using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.InboundLogistics;

/// <summary>One purchase order line's share of a goods receipt, as the settlement sees it.</summary>
public sealed record ReceiptSettlementLine(long PurchaseOrderLineId, decimal Quantity);

/// <summary>The receipt that is settling, plus the lines it received.</summary>
public sealed record ReceiptSettlementContext(
    long BusinessUnitId,
    long GoodsReceiptId,
    string ReceiptNumber,
    long SupplierPurchaseOrderId,
    DateTime ReceivedOn,
    string Actor,
    string CorrelationId,
    string IdempotencyKey,
    IReadOnlyCollection<ReceiptSettlementLine> Lines);

/// <summary>One shipment the receipt touched, and what it did to it.</summary>
public sealed record ReceiptSettlementOutcome(
    long ShipmentId,
    string ShipmentNumber,
    decimal QuantityApplied,
    string ReceiptState,
    bool MilestoneStamped);

/// <summary>
/// The Gate 4 / Gate 5 seam, in the direction that is actually safe to automate.
///
/// <para><b>The decision.</b> Arriving at the warehouse does NOT raise a goods receipt. The goods
/// receipt stays a deliberate human act, and this is what crosses back the other way: posting a
/// receipt settles the inbound shipments that were carrying the material, and stamps
/// RECEIVED_AT_WAREHOUSE on any shipment the receipt has now fully taken in.</para>
///
/// <para><b>Why not the other way round.</b> A receipt needs four facts a milestone does not
/// carry — the counted quantity (which is precisely where a shortage or damage is discovered), the
/// receiving warehouse, a receipt number, and a lot declaration.
/// <c>MaterialLotRecorder.ResolveLotNumbers</c> REFUSES a batch-tracked line with no supplier batch
/// number and a serial-tracked line without one serial per unit, so a milestone that tried to raise
/// a receipt would either have to invent those identifiers or crash the milestone write. Inventing
/// them would poison FR-MTR-02 certificates and FR-MTR-05 recall for the life of the tenant. The
/// model already keeps the two events apart on purpose: <c>PutawayLeadDays</c> is the working days
/// between goods REACHING the warehouse and being available to promise, which is exactly the count
/// and put-away the receipt performs. And a receipt moves <c>QtyOnHand</c>: a logistics status
/// update that silently changed stock on hand would turn a mis-keyed milestone into a financial
/// misstatement.</para>
///
/// <para><b>Allocation is oldest shipment first.</b> A receipt names purchase order lines, not
/// shipments, so when a line is split across several in-flight shipments something has to decide
/// which one the counted units came from. Oldest first is the convention that matches how a
/// warehouse actually clears a backlog, and it is stated in the event payload so the attribution is
/// auditable rather than implicit. A cancelled shipment is never allocated to — its quantity was
/// already released back to the purchase order line.</para>
///
/// <para>Joins the caller's ambient transaction and does not commit. Stock, the lot that explains
/// it and the shipment that carried it commit or fail together.</para>
/// </summary>
public interface IInboundShipmentReceiptSettlement
{
    Task<IReadOnlyList<ReceiptSettlementOutcome>> SettleAsync(
        ReceiptSettlementContext context, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class InboundShipmentReceiptSettlement(ErpRfqAutomationContext db)
    : IInboundShipmentReceiptSettlement
{
    public const string ReceiptSettledEvent = "INBOUND_SHIPMENT_RECEIPTED";

    public async Task<IReadOnlyList<ReceiptSettlementOutcome>> SettleAsync(
        ReceiptSettlementContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.BusinessUnitId <= 0)
            throw new InboundLogisticsValidationException(
                "A valid tenant is required to settle a receipt against its shipments.");
        if (context.Lines.Count == 0) return [];

        // Only shipments that still hold quantity. A cancelled one gave its quantity back at
        // cancellation, so allocating a receipt to it would re-inflate a total that was released.
        var shipments = await db.Set<SupplierShipment>().Include(x => x.Lines)
            .Where(x => x.BusinessUnitId == context.BusinessUnitId
                        && x.SupplierPurchaseOrderId == context.SupplierPurchaseOrderId
                        && x.Milestone != InboundShipmentMilestones.Cancelled)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (shipments.Count == 0) return [];

        var policy = await db.Set<InboundLogisticsPolicy>().AsNoTracking()
                         .SingleOrDefaultAsync(x => x.BusinessUnitId == context.BusinessUnitId, cancellationToken)
                     ?? InboundLogisticsPolicy.DefaultFor(context.BusinessUnitId);

        var now = DateTime.UtcNow;
        var actor = context.Actor.Trim();
        var receivedOn = DateOnly.FromDateTime(context.ReceivedOn);
        var applied = new Dictionary<long, decimal>();
        var touched = new List<SupplierShipment>();

        foreach (var received in context.Lines)
        {
            var remaining = received.Quantity;
            if (remaining <= 0m) continue;

            foreach (var shipment in shipments)
            {
                if (remaining <= 0m) break;
                foreach (var line in shipment.Lines
                             .Where(x => x.SupplierPurchaseOrderLineId == received.PurchaseOrderLineId)
                             .OrderBy(x => x.Id))
                {
                    if (remaining <= 0m) break;
                    var capacity = line.OutstandingReceiptQuantity;
                    if (capacity <= 0m) continue;

                    var take = Math.Min(capacity, remaining);
                    line.ReceivedQuantity += take;
                    line.Version++;
                    remaining -= take;
                    applied[shipment.Id] = applied.GetValueOrDefault(shipment.Id) + take;
                    if (!touched.Contains(shipment)) touched.Add(shipment);
                }
            }

            // A surplus is NOT an error here and is deliberately not forced onto a shipment. Goods
            // can legitimately be received against a purchase order line that nobody recorded a
            // shipment for — a domestic collection, or a line the buyer never tracked — and the
            // receipt itself already enforced the ordered-quantity ceiling. Silently inflating a
            // shipment's received quantity past what it carried would be the worse answer.
        }

        if (touched.Count == 0) return [];

        var outcomes = new List<ReceiptSettlementOutcome>(touched.Count);
        foreach (var shipment in touched)
        {
            var quantity = applied.GetValueOrDefault(shipment.Id);
            var fullyReceipted = shipment.OutstandingReceiptQuantity <= 0m;

            // The milestone follows the receipt, never the other way round, and only when the
            // receipt has taken in everything the shipment carried. A part receipt leaves the
            // shipment where it is, with its outstanding quantity visible on the panel.
            var stamped = false;
            if (fullyReceipted
                && InboundShipmentMilestones.CanTransition(
                    shipment.Milestone, InboundShipmentMilestones.ReceivedAtWarehouse))
            {
                // Never backwards past a milestone already recorded: a receipt keyed with an early
                // date must not claim the goods reached the warehouse before they cleared customs.
                var floor = shipment.LatestRecordedMilestoneDate();
                var occurredOn = floor is { } previous && receivedOn < previous ? previous : receivedOn;

                shipment.Milestone = InboundShipmentMilestones.ReceivedAtWarehouse;
                shipment.MilestoneOccurredOn = occurredOn;
                shipment.StampMilestoneDate(InboundShipmentMilestones.ReceivedAtWarehouse, occurredOn);
                stamped = true;
            }

            MaterialAvailabilityCalculator.Apply(shipment,
                MaterialAvailabilityCalculator.Derive(shipment, policy), now);
            shipment.Version++;
            shipment.ModifiedOn = now;
            shipment.ModifiedBy = actor;

            db.ProcurementEvents.Add(new ProcurementEvent
            {
                BusinessUnitId = context.BusinessUnitId,
                AggregateType = InboundShipmentApplicationService.AggregateType,
                AggregateId = shipment.Id,
                AggregateVersion = shipment.Version,
                EventType = ReceiptSettledEvent,
                Actor = actor,
                CorrelationId = context.CorrelationId.Trim(),
                // Namespaced on the shipment so one receipt covering three shipments writes three
                // events without colliding on the unique (BusinessUnitId, EventType, IdempotencyKey)
                // index, while a replayed receipt still cannot write them twice.
                IdempotencyKey = $"{context.IdempotencyKey.Trim()}:shipment:{shipment.Id}",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    goodsReceiptId = context.GoodsReceiptId,
                    receiptNumber = context.ReceiptNumber,
                    quantityApplied = quantity,
                    allocation = "OLDEST_SHIPMENT_FIRST",
                    receiptState = shipment.ReceiptState,
                    outstandingReceiptQuantity = shipment.OutstandingReceiptQuantity,
                    milestone = stamped ? InboundShipmentMilestones.ReceivedAtWarehouse : null,
                    occurredOn = stamped ? shipment.MilestoneOccurredOn : (DateOnly?)null,
                    note = stamped
                        ? $"Goods receipt {context.ReceiptNumber} booked in the whole shipment."
                        : $"Goods receipt {context.ReceiptNumber} booked in {quantity:0.####} unit(s); "
                          + $"{shipment.OutstandingReceiptQuantity:0.####} still outstanding.",
                    materialAvailableDate = shipment.MaterialAvailableDate
                }),
                OccurredOn = now
            });

            outcomes.Add(new ReceiptSettlementOutcome(
                shipment.Id, shipment.ShipmentNumber, quantity, shipment.ReceiptState, stamped));
        }

        return outcomes;
    }
}
