using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Procurement;

public sealed record ProductPurchaseEvidence(decimal UnitCost, string? CurrencyCode, DateTime PurchasedOn);

/// <summary>One purchase-price source for product views: dispatched supplier PO evidence, never master price fallbacks.</summary>
public static class ProductPurchaseEvidenceReader
{
    public static async Task<IReadOnlyDictionary<long, ProductPurchaseEvidence>> ReadLatestAsync(
        ErpRfqAutomationContext db, long businessUnitId, IReadOnlyCollection<long> productIds,
        CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0) return new Dictionary<long, ProductPurchaseEvidence>();

        // Include settled purchases as well as dispatched commitments. Internal intentions
        // and rejected/cancelled orders do not establish a purchase cost.
        var purchasedStatuses = SupplierPurchaseOrderStatuses.CommittedSupply
            .Concat(new[] { SupplierPurchaseOrderStatuses.Received, SupplierPurchaseOrderStatuses.Closed })
            .ToArray();
        var rows = await (
            from line in db.SupplierPurchaseOrderLines.AsNoTracking()
            join order in db.SupplierPurchaseOrders.AsNoTracking()
                on line.SupplierPurchaseOrderId equals order.Id
            join currency in db.Currencies.AsNoTracking().Where(c => c.BusinessUnitId == businessUnitId)
                on order.CurrencyId equals currency.Id into currencies
            from currency in currencies.DefaultIfEmpty()
            where line.BusinessUnitId == businessUnitId && order.BusinessUnitId == businessUnitId
                && productIds.Contains(line.ProductId) && purchasedStatuses.Contains(order.Status)
                && order.AcknowledgementStatus != SupplierAcknowledgementStatuses.Rejected
            select new
            {
                line.ProductId, line.UnitCost, LineId = line.Id, OrderId = order.Id,
                CurrencyCode = currency != null ? currency.Code : null,
                PurchasedOn = order.SentToSupplierOn ?? order.CreatedOn
            })
            .GroupBy(p => p.ProductId)
            .Select(g => g.OrderByDescending(p => p.PurchasedOn)
                .ThenByDescending(p => p.OrderId).ThenByDescending(p => p.LineId).First())
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(p => p.ProductId,
            p => new ProductPurchaseEvidence(p.UnitCost, p.CurrencyCode, p.PurchasedOn));
    }
}
