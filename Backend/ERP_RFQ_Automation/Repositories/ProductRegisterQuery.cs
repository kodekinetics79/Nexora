using System.Linq.Expressions;
using ERP_RFQ_Automation.DTOs.ProductDTOs;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Repositories;

/// <summary>
/// Database-side read index for the Products register. Stock comes from inventory rows,
/// not the legacy product master balance. Only the selected page is materialized.
/// </summary>
internal static class ProductRegisterQuery
{
    internal sealed class Row
    {
        public Product Product { get; init; } = null!;
        public decimal OnHand { get; init; }
        public decimal Available { get; init; }
        public bool LowStock { get; init; }
        public decimal? LastPurchaseCost { get; init; }
    }

    public static IQueryable<Row> Build(ErpRfqAutomationContext db, IQueryable<Product> products,
        long tenant, ProductListQuery options)
    {
        if (options.ValidationError is { } error) throw new ArgumentException(error, nameof(options));

        var stock = db.Set<Models.Inventory>().AsNoTracking().Where(s => s.Buid == tenant
            && (!options.WarehouseId.HasValue || s.WarehouseId == options.WarehouseId));
        var inbound = db.IncomingInventory.AsNoTracking().Where(i => i.BusinessUnitId == tenant
            && (!options.WarehouseId.HasValue || i.WarehouseId == options.WarehouseId)
            && (i.Status == IncomingInventoryStatus.Ordered || i.Status == IncomingInventoryStatus.Confirmed
                || i.Status == IncomingInventoryStatus.InTransit || i.Status == IncomingInventoryStatus.PartiallyReceived));
        var reservations = db.StockReservations.AsNoTracking().Where(r => r.BusinessUnitId == tenant
            && r.Status == StockReservationStatus.Active);

        var stockIndex = stock.Select(s => new
        {
            s.ProductId, s.WarehouseId, s.QtyOnHand, s.MinimumLevel, s.ReorderPoint,
            Available = InventoryQuantityMath.AvailableToPromise(s.QtyOnHand,
                InventoryReleaseScope.ReservationsEnabled
                    ? reservations.Where(r => r.InventoryId == s.Id).Sum(r => r.Quantity) : 0m,
                s.AllocatedQuantity, s.QuarantineQuantity, s.DamagedQuantity, s.ExpiredQuantity, s.SafetyStockQuantity),
            Incoming = inbound.Where(i => i.ProductId == s.ProductId && i.WarehouseId == s.WarehouseId)
                .Sum(i => Math.Max(0m, i.OrderedQuantity - i.ReceivedQuantity - i.AllocatedQuantity)),
        });
        // An expression visitor, not AsEnumerable/Compile: the canonical arithmetic is
        // expanded into the provider expression and remains entirely database-translatable.
        stockIndex = InlineAvailability(stockIndex);

        if (options.WarehouseId.HasValue)
            products = products.Where(p => p.WarehouseId == options.WarehouseId
                || stock.Any(s => s.ProductId == p.Id) || inbound.Any(i => i.ProductId == p.Id));

        var purchasedStatuses = SupplierPurchaseOrderStatuses.CommittedSupply
            .Concat(new[] { SupplierPurchaseOrderStatuses.Received, SupplierPurchaseOrderStatuses.Closed }).ToArray();
        var purchases = from line in db.SupplierPurchaseOrderLines.AsNoTracking()
            join order in db.SupplierPurchaseOrders.AsNoTracking() on line.SupplierPurchaseOrderId equals order.Id
            where line.BusinessUnitId == tenant && order.BusinessUnitId == tenant
                && purchasedStatuses.Contains(order.Status)
                && order.AcknowledgementStatus != SupplierAcknowledgementStatuses.Rejected
            select new { line.ProductId, line.UnitCost, LineId = line.Id, OrderId = order.Id,
                PurchasedOn = order.SentToSupplierOn ?? order.CreatedOn };

        var rows = products.Select(p => new Row
        {
            Product = p,
            OnHand = stockIndex.Where(s => s.ProductId == p.Id).Sum(s => s.QtyOnHand),
            Available = stockIndex.Where(s => s.ProductId == p.Id).Sum(s => s.Available),
            // Same shortage rungs as ReorderAlertService.Classify: an inbound commitment
            // covering the threshold is not a reorder shortage. Overstock is excluded.
            LowStock = stockIndex.Any(s => s.ProductId == p.Id
                && ((s.MinimumLevel.HasValue && s.MinimumLevel.Value > 0m
                        && s.Available + s.Incoming < s.MinimumLevel.Value)
                    || (s.ReorderPoint > 0m && s.Available + s.Incoming <= s.ReorderPoint))),
            LastPurchaseCost = purchases.Where(x => x.ProductId == p.Id)
                .OrderByDescending(x => x.PurchasedOn).ThenByDescending(x => x.OrderId)
                .ThenByDescending(x => x.LineId).Select(x => (decimal?)x.UnitCost).FirstOrDefault(),
        });

        return options.Stock switch
        {
            "in-stock" => rows.Where(r => r.OnHand > 0m),
            "out-of-stock" => rows.Where(r => r.OnHand <= 0m),
            "low-stock" => rows.Where(r => r.LowStock),
            _ => rows,
        };
    }

    public static IOrderedQueryable<Row> Order(IQueryable<Row> rows, ProductListQuery options) => options.SortBy switch
    {
        "name" => Sort(rows, r => r.Product.ProductName, options),
        "unit" => Sort(rows, r => r.Product.Uom == null ? null : r.Product.Uom.UomName, options),
        "onHand" => Sort(rows, r => r.OnHand, options),
        "available" => Sort(rows, r => r.Available, options),
        "lastPurchaseCost" => Sort(rows, r => r.LastPurchaseCost, options),
        "landedCost" => Sort(rows, r => r.Product.UnitCost, options),
        "salePrice" => Sort(rows, r => r.Product.SellingPrice, options),
        "currency" => Sort(rows, r => r.Product.PriceCurrency == null ? null : r.Product.PriceCurrency.Code, options),
        _ => Sort(rows, r => r.Product.PartNo, options),
    };

    private static IOrderedQueryable<Row> Sort<T>(IQueryable<Row> rows, Expression<Func<Row, T>> key,
        ProductListQuery options)
    {
        // Missing values consistently last, independent of provider defaults and direction.
        var isNull = Expression.Lambda<Func<Row, bool>>(
            Expression.Equal(Expression.Convert(key.Body, typeof(object)), Expression.Constant(null)), key.Parameters);
        var ordered = rows.OrderBy(isNull);
        return (options.SortDirection == "desc" ? ordered.ThenByDescending(key) : ordered.ThenBy(key))
            .ThenBy(r => r.Product.Id);
    }

    private static IQueryable<T> InlineAvailability<T>(IQueryable<T> query) =>
        query.Provider.CreateQuery<T>(new AvailabilityInliner().Visit(query.Expression)!);

    private sealed class AvailabilityInliner : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType != typeof(InventoryQuantityMath)
                || node.Method.Name != nameof(InventoryQuantityMath.AvailableToPromise)) return base.VisitMethodCall(node);
            var canonical = InventoryQuantityMath.AvailableToPromiseExpression;
            return new ParameterSubstitution(canonical.Parameters.Zip(node.Arguments)
                .ToDictionary(x => x.First, x => x.Second)).Visit(canonical.Body)!;
        }
    }

    private sealed class ParameterSubstitution(Dictionary<ParameterExpression, Expression> values) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => values.GetValueOrDefault(node, node);
    }
}
