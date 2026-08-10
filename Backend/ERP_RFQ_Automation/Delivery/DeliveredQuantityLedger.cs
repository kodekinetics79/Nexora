using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Delivery;

/// <summary>
/// FR-DLM-02, per order line: what was awarded, what has left, and what the customer has.
/// </summary>
/// <param name="OrderItemId">The awarded line.</param>
/// <param name="AwardedQuantity">The ordered quantity — the ceiling every other figure sits under.</param>
/// <param name="DespatchedQuantity">
/// Cumulative across every active shipment of this order whose delivery status is in
/// <see cref="DeliveryStatuses.Despatched"/>. This is what left the warehouse.
/// </param>
/// <param name="AcceptedQuantity">
/// Cumulative across every proof of delivery recorded against those shipments. This is what the
/// customer signed for, and it is the number that governs invoicing and fulfilment.
/// </param>
/// <param name="AwaitingConfirmationQuantity">
/// Despatched but not yet confirmed either way. Deliberately a third figure rather than being
/// folded into either of the others: goods on a lorry are neither in the warehouse nor with the
/// customer, and reporting them as one or the other is how a delivery dispute starts.
/// </param>
/// <param name="RefusedQuantity">
/// Despatched, confirmed, and NOT taken — short, damaged, rejected or lost. FR-DLM-07's number.
/// </param>
public sealed record DeliveredQuantityLine(
    long OrderItemId,
    decimal AwardedQuantity,
    decimal DespatchedQuantity,
    decimal AcceptedQuantity,
    decimal AwaitingConfirmationQuantity,
    decimal RefusedQuantity)
{
    /// <summary>
    /// Still owed to the customer: awarded, less what they have, less what is on its way.
    ///
    /// <para>Refused units come BACK into this figure, because a refused unit is a unit the
    /// customer ordered and does not have. Whether it is re-supplied or credited is the commercial
    /// decision recorded on the shortfall — see <see cref="DeliveryShortfallDecisions"/> — and
    /// until that decision is taken the line is genuinely outstanding.</para>
    /// </summary>
    public decimal OutstandingQuantity =>
        AwardedQuantity - AcceptedQuantity - AwaitingConfirmationQuantity;

    /// <summary>The customer has everything they were awarded.</summary>
    public bool IsFullyDelivered => AcceptedQuantity >= AwardedQuantity;
}

/// <summary>
/// The delivery position of one order line, in the shape the invoice ceiling needs.
/// </summary>
/// <param name="DespatchedQuantity">Cumulative units that have left the warehouse.</param>
/// <param name="AcceptedQuantity">Cumulative units a customer has signed for.</param>
public sealed record DeliveredQuantityCap(decimal DespatchedQuantity, decimal AcceptedQuantity)
{
    /// <summary>
    /// Whether this module knows anything at all about the line.
    ///
    /// <para>This distinction is the boundary of the delivered-quantity ceiling, and it is stated
    /// here rather than implied at the call site. Once a single unit has left against a line, the
    /// delivery record is the authority on how much of it the customer has, and only accepted units
    /// are billable. A line with no despatch at all is not something this module can speak to —
    /// invoicing it remains governed by the pre-existing order-status eligibility gate, exactly as
    /// before this gate. See the note on the ceiling in
    /// <c>CommercialFinanceApplicationService.CreateInvoiceAsync</c>.</para>
    /// </summary>
    public bool HasDeliveryActivity => DespatchedQuantity > 0m || AcceptedQuantity > 0m;
}

public interface IDeliveredQuantityLedger
{
    /// <summary>Every awarded line on the order, whether or not anything has shipped against it.</summary>
    Task<IReadOnlyList<DeliveredQuantityLine>> ForOrderAsync(
        long businessUnitId, long orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepted quantity keyed by order line, for the named lines only. This is the shape the
    /// invoice ceiling needs and it exists so that path does not have to know how the number is
    /// built.
    /// </summary>
    Task<IReadOnlyDictionary<long, decimal>> AcceptedByOrderItemAsync(
        long businessUnitId, IReadOnlyCollection<long> orderItemIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Despatched and accepted per order line, for the named lines only. This is the shape the
    /// invoice ceiling needs, and it exists so that path does not have to know how either number is
    /// built — or how to tell "nothing delivered" from "nothing accepted".
    /// </summary>
    Task<IReadOnlyDictionary<long, DeliveredQuantityCap>> CapsByOrderItemAsync(
        long businessUnitId, IReadOnlyCollection<long> orderItemIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Gate 7 / FR-DLM-02 — the cumulative delivered ledger. Register item E50 recorded its absence:
/// <c>DeliveredQuantity</c>, <c>CumulativeDelivered</c>, <c>QuantityDelivered</c> and
/// <c>ShippedQuantity</c> all returned zero hits in the backend, and <c>ShipmentItem</c> carried
/// only what was loaded. Nothing in the system could answer "how much of this order does the
/// customer actually have".
///
/// <para><b>THE DECISION: "delivered" means CONFIRMED-RECEIVED, and it is carried as a second
/// number rather than replacing the first.</b></para>
///
/// <para>A delivery note evidences a despatch: goods left, stock moved, and the trader has done
/// something it can charge for having done. Confirmed receipt is a different fact, evidenced by a
/// different document — the POD, with a name, a signature and a time. In a commercial dispute the
/// only one of those two that decides who is right is the second. So they are two numbers, and
/// this ledger carries both:</para>
///
/// <list type="bullet">
/// <item><b>Despatched</b> — sum of shipment-line quantities on shipments whose governed status is
/// in <see cref="DeliveryStatuses.Despatched"/>. Drives the over-shipment ceiling and the goods
/// issue, because those are facts about the warehouse.</item>
/// <item><b>Accepted</b> — sum of <see cref="DeliveryProofLine.AcceptedQuantity"/>, which exists
/// only where a POD exists. Drives invoicing and fulfilment, because those are facts about the
/// customer.</item>
/// </list>
///
/// <para><b>Why not one number.</b> Collapsing them either bills for goods on a lorry, or refuses
/// to bill for goods a customer signed for last week because nobody has closed the order. Both are
/// wrong, and each is wrong in a direction somebody notices only after it has cost money. Carrying
/// two also makes the gap between them visible as
/// <see cref="DeliveredQuantityLine.AwaitingConfirmationQuantity"/>, which is exactly the queue an
/// operations person needs: consignments that went out and nobody has confirmed.</para>
///
/// <para><b>Cancelled and inactive shipments net off, and they do so by construction.</b> A
/// cancelled shipment is not in <see cref="DeliveryStatuses.Despatched"/>, so it contributes no
/// despatched quantity, and cancellation is unreachable from a confirmed state
/// (<see cref="DeliveryStatuses.Cancellable"/>), so it can never have contributed an accepted
/// quantity to subtract. Soft-deleted rows (<c>IsActive = false</c>) are excluded on both sides.
/// There is no draft shipment in this product: a shipment row exists because someone raised a
/// despatch, and the pre-despatch state is SCHEDULED, which accrues nothing.</para>
///
/// <para><b>Derived, never stored.</b> There is no <c>OrderItem.DeliveredQuantity</c> column. A
/// denormalised total is a fourth writer for a fact three tables already state, and the wiring
/// contract's failure #7 is exactly the drift that follows. Both figures come from the rows that
/// evidence them, through this one service, so there is one definition of delivered in the
/// codebase and every consumer depends on it.</para>
///
/// <para><b>ZATCA seam.</b> <c>CreateInvoiceLineRequest(OrderItemId, Quantity)</c> is capped
/// against <see cref="DeliveredQuantityLine.AcceptedQuantity"/> in
/// <c>CommercialFinanceApplicationService.CreateInvoiceAsync</c>. When FR-DLM-04 lands, the cleared
/// tax invoice is generated from that already-capped document, so no ZATCA code needs to know this
/// ledger exists.</para>
/// </summary>
public sealed class DeliveredQuantityLedger(ErpRfqAutomationContext db) : IDeliveredQuantityLedger
{
    private readonly ErpRfqAutomationContext _db = db;

    public async Task<IReadOnlyList<DeliveredQuantityLine>> ForOrderAsync(
        long businessUnitId, long orderId, CancellationToken cancellationToken = default)
    {
        if (businessUnitId <= 0)
            throw new UnauthorizedAccessException("A valid authenticated tenant is required.");

        var awarded = await _db.Set<OrderItem>().AsNoTracking()
            .Where(i => i.OrderId == orderId && i.IsActive
                        && _db.Orders.Any(o => o.Id == orderId && o.BusinessUnitId == businessUnitId))
            .Select(i => new { i.Id, i.Quantity })
            .ToListAsync(cancellationToken);
        if (awarded.Count == 0) return [];

        // Despatched: shipment lines on shipments the goods have physically left on.
        var despatched = await _db.ShipmentItems.AsNoTracking()
            .Where(si => si.IsActive && si.Shipment.IsActive
                         && si.Shipment.OrderId == orderId
                         && si.Shipment.BusinessUnitId == businessUnitId
                         && DeliveryStatuses.DespatchedForQuery.Contains(si.Shipment.DeliveryStatus))
            .GroupBy(si => si.OrderItemId)
            .Select(g => new { OrderItemId = g.Key, Quantity = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.OrderItemId, x => x.Quantity, cancellationToken);

        // Accepted and refused: what a POD says the customer took, and did not take.
        var confirmed = await _db.DeliveryProofLines.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId && l.OrderId == orderId)
            .GroupBy(l => l.OrderItemId)
            .Select(g => new
            {
                OrderItemId = g.Key,
                Accepted = g.Sum(x => x.AcceptedQuantity),
                Despatched = g.Sum(x => x.DespatchedQuantity)
            })
            .ToDictionaryAsync(x => x.OrderItemId, x => x, cancellationToken);

        return awarded.Select(line =>
        {
            var out_ = despatched.GetValueOrDefault(line.Id);
            confirmed.TryGetValue(line.Id, out var proof);
            var accepted = proof?.Accepted ?? 0m;
            var confirmedDespatch = proof?.Despatched ?? 0m;
            return new DeliveredQuantityLine(
                line.Id,
                line.Quantity,
                out_,
                accepted,
                // Everything that left and has no POD behind it yet. Never negative: a confirmed
                // shipment is by definition in the despatched set, so the subtraction cannot invert.
                Math.Max(0m, out_ - confirmedDespatch),
                confirmedDespatch - accepted);
        }).ToList();
    }

    public async Task<IReadOnlyDictionary<long, decimal>> AcceptedByOrderItemAsync(
        long businessUnitId, IReadOnlyCollection<long> orderItemIds,
        CancellationToken cancellationToken = default)
    {
        if (businessUnitId <= 0)
            throw new UnauthorizedAccessException("A valid authenticated tenant is required.");
        if (orderItemIds is null || orderItemIds.Count == 0)
            return new Dictionary<long, decimal>();

        var ids = orderItemIds.Distinct().ToArray();
        return await _db.DeliveryProofLines.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId && ids.Contains(l.OrderItemId))
            .GroupBy(l => l.OrderItemId)
            .Select(g => new { OrderItemId = g.Key, Accepted = g.Sum(x => x.AcceptedQuantity) })
            .ToDictionaryAsync(x => x.OrderItemId, x => x.Accepted, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<long, DeliveredQuantityCap>> CapsByOrderItemAsync(
        long businessUnitId, IReadOnlyCollection<long> orderItemIds,
        CancellationToken cancellationToken = default)
    {
        if (businessUnitId <= 0)
            throw new UnauthorizedAccessException("A valid authenticated tenant is required.");
        if (orderItemIds is null || orderItemIds.Count == 0)
            return new Dictionary<long, DeliveredQuantityCap>();

        var ids = orderItemIds.Distinct().ToArray();

        // DespatchedForQuery, not the IReadOnlySet: EF cannot translate set membership, and this
        // predicate has to reach SQL. The rule is still declared once in DeliveryStatuses; only the
        // shape handed to the expression tree differs.
        var despatched = await _db.ShipmentItems.AsNoTracking()
            .Where(si => si.IsActive && si.Shipment.IsActive
                         && si.Shipment.BusinessUnitId == businessUnitId
                         && ids.Contains(si.OrderItemId)
                         && DeliveryStatuses.DespatchedForQuery.Contains(si.Shipment.DeliveryStatus))
            .GroupBy(si => si.OrderItemId)
            .Select(g => new { OrderItemId = g.Key, Quantity = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.OrderItemId, x => x.Quantity, cancellationToken);

        var accepted = await AcceptedByOrderItemAsync(businessUnitId, ids, cancellationToken);

        return despatched.Keys.Union(accepted.Keys).ToDictionary(
            orderItemId => orderItemId,
            orderItemId => new DeliveredQuantityCap(
                despatched.GetValueOrDefault(orderItemId), accepted.GetValueOrDefault(orderItemId)));
    }
}
